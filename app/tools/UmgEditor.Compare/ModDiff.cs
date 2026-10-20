using System.Text;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmgEditor.Core;
using UmgEditor.Core.Animation;

/// <summary>What a mod changes against the stock build it targets: every package its containers serve, stock vs modded, read-only.</summary>
static class ModDiff
{
    public sealed record KeyDiff(string Animation, string Widget, string Track, string Channel, string Stock, string Mod);
    public sealed record PropDiff(string Export, string Path, string Stock, string Mod);
    public sealed record TextureInfo(string Package, string Stock, string Mod);

    public sealed class Result
    {
        public required string ModDir { get; init; }
        public List<string> ModContainers { get; } = [];
        public List<string> NewPackages { get; } = [];
        public List<string> NotServedByMod { get; } = [];
        public List<PackageDiff> Widgets { get; } = [];
        public Dictionary<string, List<KeyDiff>> AnimationKeys { get; } = [];
        public Dictionary<string, List<string>> AnimationsAddedRemoved { get; } = [];
        public Dictionary<string, List<PropDiff>> OtherPackages { get; } = [];
        public List<TextureInfo> Textures { get; } = [];
        public List<string> LooseFiles { get; } = [];
        public List<string> Errors { get; } = [];
    }

    public static Result Run(Side stock, Side modded, string modDir, IProgress<string>? log = null)
    {
        var r = new Result { ModDir = modDir };
        var modFiles = new DirectoryInfo(modDir).GetFiles().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        r.ModContainers.AddRange(modFiles.Order());
        var served = modded.Source.Provider.Files.Keys
            .Where(k => modFiles.Contains(modded.Source.ContainerOf(k)))
            .Distinct(StringComparer.Ordinal) // the provider lists a key once per container that has it
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        log?.Report($"{served.Count} files served by the mod's containers");

        foreach (var key in served)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                if (!key.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase) && !key.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                    r.LooseFiles.Add($"{key} ({modded.Source.Provider.Files[key].Size:N0} B; stock {(stock.Source.Provider.Files.TryGetValue(key, out var s) ? s.Size.ToString("N0") + " B" : "none")})");
                continue;
            }
            var path = GamePaths.ToPackagePath(key);
            if (!stock.Source.Provider.Files.ContainsKey(key)) { r.NewPackages.Add(path); continue; }
            try
            {
                if (modded.Widgets.ContainsKey(path)) Widget(r, stock, modded, path);
                else if (TextureOf(modded, path) is { } mt) r.Textures.Add(new(path, TextureOf(stock, path) ?? "?", mt));
                else Other(r, stock, modded, path);
            }
            catch (Exception e) { r.Errors.Add($"{path}: {e.GetType().Name}: {e.Message}"); }
        }
        return r;
    }

    private static void Widget(Result r, Side stock, Side modded, string path)
    {
        r.Widgets.Add(PackageDiff.Of(path, stock, modded));
        var a = AnimationReader.Read(stock.Source, path).ToDictionary(x => x.Name);
        var b = AnimationReader.Read(modded.Source, path).ToDictionary(x => x.Name);
        var addRemove = a.Keys.Except(b.Keys).Select(n => "removed " + n).Concat(b.Keys.Except(a.Keys).Select(n => "added " + n)).ToList();
        if (addRemove.Count > 0) r.AnimationsAddedRemoved[path] = addRemove;

        var diffs = new List<KeyDiff>();
        foreach (var name in a.Keys.Intersect(b.Keys).Order())
        {
            var (sa, sb) = (a[name], b[name]);
            if (sa.Start != sb.Start || sa.End != sb.End)
                diffs.Add(new(name, "(playback)", "", "", $"{sa.Seconds(sa.Start):0.###}..{sa.Seconds(sa.End):0.###}s", $"{sb.Seconds(sb.Start):0.###}..{sb.Seconds(sb.End):0.###}s"));
            var ca = Channels(sa);
            var cb = Channels(sb);
            foreach (var k in ca.Keys.Union(cb.Keys).Order())
            {
                var x = ca.GetValueOrDefault(k) ?? "-";
                var y = cb.GetValueOrDefault(k) ?? "-";
                if (x == y) continue;
                var p = k.Split('|');
                diffs.Add(new(name, p[0], p[1], p[2], x, y));
            }
        }
        if (diffs.Count > 0) r.AnimationKeys[path] = diffs;
    }

    /// <summary>"widget|track property|channel" → "0s=1, 0.5s=2 (compiled same)"; one key per bound widget and channel.</summary>
    private static Dictionary<string, string> Channels(AnimationInfo anim)
    {
        var map = new Dictionary<string, string>();
        foreach (var t in anim.Tracks)
            foreach (var c in t.Channels.Where(c => c.Keys.Count > 0 || c.Default != null))
            {
                var key = $"{t.Widget}{(t.OnSlot ? " [slot]" : "")}|{t.Track} {t.Property}|{c.Path}";
                var text = string.Join("  ", c.Keys.Select(k => $"{k.Seconds:0.###}s={k.Value}")) + (c.Default != null ? $" default={c.Default}" : "")
                    + (c.Compiled == "same" ? "" : $" ({c.Compiled})");
                map[key] = map.TryGetValue(key, out var prev) ? prev + " ; " + text : text;
            }
        return map;
    }

    private static string? TextureOf(Side side, string path)
    {
        var pkg = side.Source.LoadPackage(path);
        var tex = pkg.GetExports().OfType<UTexture2D>().FirstOrDefault();
        if (tex == null) return null;
        var mip = tex.GetFirstMip();
        return $"{tex.Format} {mip?.SizeX}x{mip?.SizeY}, {tex.PlatformData?.Mips?.Length ?? 0} mips";
    }

    private static void Other(Result r, Side stock, Side modded, string path)
    {
        var a = Flatten(stock, path);
        var b = Flatten(modded, path);
        var diffs = new List<PropDiff>();
        foreach (var k in a.Keys.Union(b.Keys).Order())
        {
            var x = a.GetValueOrDefault(k) ?? "-";
            var y = b.GetValueOrDefault(k) ?? "-";
            if (x == y) continue;
            var sep = k.IndexOf('|');
            diffs.Add(new(k[..sep], k[(sep + 1)..], x, y));
        }
        r.OtherPackages[path] = diffs;
    }

    /// <summary>"Export|Properties.a.b[2]" → leaf value, for every export of a package (references printed as names).</summary>
    private static Dictionary<string, string> Flatten(Side side, string path)
    {
        var map = new Dictionary<string, string>();
        foreach (var e in side.Source.LoadPackage(path).GetExports())
        {
            var o = JObject.Parse(JsonConvert.SerializeObject(e));
            o.Remove("Outer"); o.Remove("Flags");
            void Walk(JToken t, string p)
            {
                switch (t)
                {
                    case JObject j when j["ObjectName"] != null && j["ObjectPath"] != null: map[e.Name + "|" + p] = j["ObjectName"]!.ToString(); break;
                    case JObject j: foreach (var prop in j.Properties()) Walk(prop.Value, p.Length == 0 ? prop.Name : p + "." + prop.Name); break;
                    case JArray arr: for (int i = 0; i < arr.Count; i++) Walk(arr[i], $"{p}[{i}]"); break;
                    default: map[e.Name + "|" + p] = t.ToString(Formatting.None); break;
                }
            }
            Walk(o, "");
        }
        return map;
    }

    public static string Report(Result r)
    {
        var sb = new StringBuilder();
        string Rel(string p) => p.Replace("/Game/Assets/ui/Data/Widget/", "").Replace("/Game/Assets/", "");
        string Cell(string s) => (s.Length > 160 ? s[..160] + "…" : s).Replace("|", "\\|").Replace("\n", " ");
        sb.AppendLine("# What the mod changes (PC stock → PC + mod)").AppendLine();
        sb.AppendLine($"Mod folder: `{r.ModDir}` ({string.Join(", ", r.ModContainers)}).").AppendLine();
        sb.AppendLine("| | count |").AppendLine("|---|---|");
        sb.AppendLine($"| widgets | {r.Widgets.Count} (unchanged: {r.Widgets.Count(w => w.Identical && !r.AnimationKeys.ContainsKey(w.Package) && !r.AnimationsAddedRemoved.ContainsKey(w.Package))}) |");
        sb.AppendLine($"| widgets with animation key changes | {r.AnimationKeys.Count} |");
        sb.AppendLine($"| textures | {r.Textures.Count} (size or format changed: {r.Textures.Count(t => t.Stock != t.Mod)}) |");
        sb.AppendLine($"| other packages | {r.OtherPackages.Count} |");
        sb.AppendLine($"| new packages (not in stock) | {r.NewPackages.Count} |");
        sb.AppendLine($"| loose files (movies, fonts…) | {r.LooseFiles.Count} |");
        sb.AppendLine($"| errors | {r.Errors.Count} |").AppendLine();
        sb.AppendLine("Values are `stock → mod`; `default X` = not stored (engine default); `-` = absent. Boxes are px at 1920×1080.").AppendLine();

        sb.AppendLine("## Widgets").AppendLine();
        foreach (var d in r.Widgets)
        {
            var keys = r.AnimationKeys.GetValueOrDefault(d.Package) ?? [];
            var addRem = r.AnimationsAddedRemoved.GetValueOrDefault(d.Package) ?? [];
            if (d.Identical && keys.Count == 0 && addRem.Count == 0) continue;
            sb.AppendLine($"### {Rel(d.Package)}").AppendLine();
            if (d.Error != null) { sb.AppendLine(d.Error).AppendLine(); continue; }
            sb.AppendLine($"exports {d.PcExports} → {d.MobileExports}").AppendLine();
            if (d.WidgetsOnlyPc.Count > 0) sb.AppendLine($"- widgets removed: {string.Join(", ", d.WidgetsOnlyPc)}");
            if (d.WidgetsOnlyMobile.Count > 0) sb.AppendLine($"- widgets added: {string.Join(", ", d.WidgetsOnlyMobile)}");
            if (addRem.Count > 0) sb.AppendLine($"- animations: {string.Join(", ", addRem)}");
            if (d.Changed.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | field | stock | mod |").AppendLine("|---|---|---|---|");
                foreach (var c in d.Changed) sb.AppendLine($"| {c.Widget} | {c.Field} | {Cell(c.Pc)} | {Cell(c.Mobile)} |");
            }
            if (d.Textures.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | stock texture | mod texture |").AppendLine("|---|---|---|");
                foreach (var t in d.Textures) sb.AppendLine($"| {t.Widget} | {Cell(t.Pc ?? "-")} | {Cell(t.Mobile ?? "-")} |");
            }
            if (d.Boxes.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | stock box | mod box | Δx | Δy | Δw | Δh |").AppendLine("|---|---|---|---|---|---|---|");
                foreach (var b in d.Boxes) sb.AppendLine($"| {b.Widget} | {b.Pc} | {b.Mobile} | {b.Dx} | {b.Dy} | {b.Dw} | {b.Dh} |");
            }
            if (keys.Count > 0)
            {
                sb.AppendLine().AppendLine("| animation | widget | track | channel | stock keys | mod keys |").AppendLine("|---|---|---|---|---|---|");
                foreach (var k in keys) sb.AppendLine($"| {k.Animation} | {k.Widget} | {k.Track} | {k.Channel} | {Cell(k.Stock)} | {Cell(k.Mod)} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Other packages").AppendLine();
        foreach (var (p, diffs) in r.OtherPackages)
        {
            sb.AppendLine($"### {Rel(p)} ({diffs.Count} values differ)").AppendLine();
            if (diffs.Count == 0) continue;
            sb.AppendLine("| export | path | stock | mod |").AppendLine("|---|---|---|---|");
            foreach (var d in diffs.Take(300)) sb.AppendLine($"| {d.Export} | {Cell(d.Path)} | {Cell(d.Stock)} | {Cell(d.Mod)} |");
            if (diffs.Count > 300) sb.AppendLine($"| … | {diffs.Count - 300} more | | |");
            sb.AppendLine();
        }
        sb.AppendLine("## Textures").AppendLine();
        sb.AppendLine("| texture | stock | mod |").AppendLine("|---|---|---|");
        foreach (var t in r.Textures) sb.AppendLine($"| {Rel(t.Package)} | {t.Stock} | {t.Mod}{(t.Stock == t.Mod ? "" : " **≠**")} |");
        sb.AppendLine();
        if (r.LooseFiles.Count > 0) { sb.AppendLine("## Loose files").AppendLine(); foreach (var f in r.LooseFiles) sb.AppendLine("- " + f); sb.AppendLine(); }
        if (r.NewPackages.Count > 0) { sb.AppendLine("## New packages").AppendLine(); foreach (var f in r.NewPackages) sb.AppendLine("- " + f); sb.AppendLine(); }
        if (r.Errors.Count > 0) { sb.AppendLine("## Errors").AppendLine(); foreach (var f in r.Errors) sb.AppendLine("- " + f); }
        return sb.ToString();
    }
}
