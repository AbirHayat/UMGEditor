using System.Globalization;
using System.Text.RegularExpressions;

namespace UmgEditor.Core.Layout;

/// <summary>Where a value used by the layout came from. Only Stored values exist in the widget's own file.</summary>
public enum ValueSource
{
    /// <summary>Read from the package (tagged property present).</summary>
    Stored,
    /// <summary>Engine class default, because the property is not stored.</summary>
    EngineDefault,
    /// <summary>Read from the game's config files.</summary>
    Config,
    /// <summary>Not knowable from the files (estimate, unsupported panel, fallback).</summary>
    Assumed,
    /// <summary>Derived from other values by the layout rules.</summary>
    Computed,
}

/// <summary>[/Script/Engine.UserInterfaceSettings]: DPI scale rule and curve, read from the game's packaged config.</summary>
public sealed partial class UiScaleSettings
{
    public string Rule { get; private init; } = "ShortestSide";
    public IReadOnlyList<(double Time, double Value)> Curve { get; private init; } = [(480, 0.444), (720, 0.666), (1080, 1.0), (8640, 8.0)];
    public double ApplicationScale { get; private init; } = 1.0;
    /// <summary>Which file each setting came from, e.g. "UIScaleCurve: Engine/Config/BaseEngine.ini".</summary>
    public IReadOnlyList<string> Origins { get; private init; } = [];
    public ValueSource Source { get; private init; } = ValueSource.Assumed;

    private static readonly string[] Files = ["Engine/Config/BaseEngine.ini", "PesMobile/Config/DefaultEngine.ini"]; // later files override

    public static UiScaleSettings Load(GameSource source)
    {
        string? rule = null, curve = null, appScale = null;
        var origins = new List<string>();
        foreach (var file in Files)
        {
            if (!source.Provider.TrySaveAsset(file, out var bytes)) continue;
            var section = ReadSection(System.Text.Encoding.UTF8.GetString(bytes), "/Script/Engine.UserInterfaceSettings");
            if (section.TryGetValue("UIScaleRule", out var r)) { rule = r; origins.Add($"UIScaleRule={r}: {file}"); }
            if (section.TryGetValue("UIScaleCurve", out var c)) { curve = c; origins.Add($"UIScaleCurve: {file}"); }
            if (section.TryGetValue("ApplicationScale", out var a)) { appScale = a; origins.Add($"ApplicationScale={a}: {file}"); }
        }
        if (rule == null && curve == null) return new UiScaleSettings { Origins = ["not found in config; UE defaults assumed"] };

        var keys = KeyRegex().Matches(curve ?? "").Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        return new UiScaleSettings
        {
            Rule = rule ?? "ShortestSide",
            Curve = keys.Count > 0 ? keys : new UiScaleSettings().Curve,
            ApplicationScale = appScale != null ? double.Parse(appScale, CultureInfo.InvariantCulture) : 1.0,
            Origins = origins,
            Source = ValueSource.Config,
        };
    }

    /// <summary>The viewport dimension the rule picks (UE: GetDPIScaleBasedOnSize).</summary>
    public double RuleInput(double width, double height) => Rule switch
    {
        "LongestSide" => Math.Max(width, height),
        "Horizontal" => width,
        "Vertical" => height,
        _ => Math.Min(width, height), // ShortestSide (default)
    };

    /// <summary>Slate DPI scale for a viewport in pixels: curve (linear keys, clamped ends) × ApplicationScale.</summary>
    public double DpiScale(double width, double height)
    {
        if (Rule is "Custom" or "ScaleToFit") return ApplicationScale; // not used by this game; kept explicit
        var x = RuleInput(width, height);
        var k = Curve;
        double v;
        if (x <= k[0].Time) v = k[0].Value;
        else if (x >= k[^1].Time) v = k[^1].Value;
        else
        {
            int i = 1;
            while (k[i].Time < x) i++;
            var t = (x - k[i - 1].Time) / (k[i].Time - k[i - 1].Time);
            v = k[i - 1].Value + t * (k[i].Value - k[i - 1].Value);
        }
        return v * ApplicationScale;
    }

    private static Dictionary<string, string> ReadSection(string ini, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inSection = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals($"[{name}]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || line.Length == 0 || line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    [GeneratedRegex(@"Time=([-\d.]+)\s*,\s*Value=([-\d.]+)")]
    private static partial Regex KeyRegex();
}
