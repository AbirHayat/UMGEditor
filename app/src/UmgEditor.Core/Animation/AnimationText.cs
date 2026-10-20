using System.Globalization;
using System.Text;

namespace UmgEditor.Core.Animation;

/// <summary>One channel that does something: a readable name and what its keys do over time.</summary>
public sealed record ChannelSummary(string Name, string Text, AnimChannel Channel);

/// <summary>
/// One animation track of a widget as the inspector shows it: what it does, and every key with its byte locations.
/// A class (reference identity): WPF keys item automation peers by Equals.
/// </summary>
public sealed class WidgetAnimationTrack(AnimationInfo animation, AnimTrack track)
{
    public string Animation => animation.Name;
    public AnimTrack Track => track;
    public string Playback => $"{AnimationText.S(animation.Seconds(animation.Start))}–{AnimationText.S(animation.Seconds(animation.End))}";
    public string TrackText => $"{(track.OnSlot ? "slot " : "")}{track.Track.Replace("Track", "")} {track.Property}".Trim();
    public IReadOnlyList<ChannelSummary> Does { get; } = AnimationText.Describe(track);
    public string DoesText => Does.Count == 0 ? "no change (neutral values only)" : string.Join("\n", Does.Select(d => $"{d.Name}: {d.Text}"));
    /// <summary>The keys of the channels that do something (all channels when none does), with byte locations.</summary>
    public string KeysText => $"section export {track.SectionExport}\n" +
        string.Join("\n", (Does.Count > 0 ? Does.Select(d => d.Channel) : track.Channels).Select(c => AnimationText.KeysWithBytes(track, c)));
}

/// <summary>
/// Readable descriptions of widget animation tracks: channel names (Offsets.Right, Translation.Y, Visibility, Opacity),
/// values (ESlateVisibility as shown/hidden) and what the keys do ("−90 → 0 over 0–0.4 s, cubic"). Channels that only
/// hold the neutral value (translation 0, scale 1, angle 0, shear 0, no keys) are left out.
/// </summary>
public static class AnimationText
{
    private static readonly string[] Visibility = ["Visible", "Collapsed", "Hidden", "HitTestInvisible", "SelfHitTestInvisible"];

    /// <summary>"Offsets.Right", "Translation.Y", "Visibility", "Opacity", …</summary>
    public static string ChannelName(AnimTrack track, string path)
    {
        var (name, index) = path.EndsWith(']') ? (path[..path.IndexOf('[')], int.Parse(path[(path.IndexOf('[') + 1)..^1], CultureInfo.InvariantCulture)) : (path, 0);
        return track.Track switch
        {
            "MarginTrack" => "Offsets." + name.Replace("Curve", ""),
            "2DTransformTrack" => name == "Rotation" ? "Angle" : $"{name}.{(index == 0 ? "X" : "Y")}",
            "ByteTrack" when track.Property == "Visibility" => "Visibility",
            "FloatTrack" when track.Property == "RenderOpacity" => "Opacity",
            "ColorTrack" => $"{track.Property}.{name.Replace("Curve", "")}",
            _ => track.Property.Length > 0 ? track.Property : name,
        };
    }

    /// <summary>A key value as text: visibility by name (shown/hidden), numbers trimmed.</summary>
    public static string ValueText(AnimTrack track, string value)
    {
        if (track.Track == "ByteTrack" && track.Property == "Visibility" && int.TryParse(value, out var v) && v >= 0 && v < Visibility.Length)
            return v is 1 or 2 ? $"hidden ({Visibility[v]})" : $"shown ({Visibility[v]})";
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Num(d) : value;
    }

    /// <summary>What a channel does, or null when it only holds the neutral value.</summary>
    public static ChannelSummary? Describe(AnimTrack track, AnimChannel channel)
    {
        var name = ChannelName(track, channel.Path);
        if (channel.Keys.Count == 0)
            return channel.Default is { } d && !IsNeutral(name, d) ? new ChannelSummary(name, $"stays {ValueText(track, d)} (default, no keys)", channel) : null;
        var values = channel.Keys.Select(k => Same(k.Value)).ToList();
        if (values.Distinct().Count() == 1)
            return IsNeutral(name, values[0]) ? null
                : new ChannelSummary(name, channel.Keys.Count == 1 ? $"set to {ValueText(track, values[0])} at {S(channel.Keys[0].Seconds)}" : $"holds {ValueText(track, values[0])} ({S(channel.Keys[0].Seconds)}–{S(channel.Keys[^1].Seconds)})", channel);

        var parts = new List<string>();
        var first = channel.Keys[0];
        if (first.Seconds > 0) parts.Add($"{ValueText(track, first.Value)} until {S(first.Seconds)}");
        for (int k = 0; k + 1 < channel.Keys.Count; k++)
        {
            AnimKey a = channel.Keys[k], b = channel.Keys[k + 1];
            if (Same(a.Value) == Same(b.Value)) { parts.Add($"holds {ValueText(track, a.Value)} {S(a.Seconds)}–{S(b.Seconds)}"); continue; }
            parts.Add(a.Interp is "Constant" || a.Interp == null
                ? $"{ValueText(track, a.Value)} → {ValueText(track, b.Value)} at {S(b.Seconds)} (jump)"
                : $"{ValueText(track, a.Value)} → {ValueText(track, b.Value)} over {S(a.Seconds)}–{S(b.Seconds)} ({a.Interp!.ToLowerInvariant()})");
        }
        return new ChannelSummary(name, string.Join("; ", parts), channel);
    }

    /// <summary>The channels of a track that do something.</summary>
    public static IReadOnlyList<ChannelSummary> Describe(AnimTrack track) =>
        track.Channels.Select(c => Describe(track, c)).OfType<ChannelSummary>().ToList();

    /// <summary>"MatchTime [slot] MarginTrack": the bound widget, and whether the track drives its slot.</summary>
    public static string Target(AnimTrack track) => track.OnSlot ? $"{track.Widget} (slot)" : track.Widget;

    /// <summary>Every key of a channel with its byte locations, one per line (for show-anim and the inspector).</summary>
    public static string KeysWithBytes(AnimTrack track, AnimChannel channel, string indent = "")
    {
        var sb = new StringBuilder();
        sb.Append(indent).Append($"{ChannelName(track, channel.Path)} [{channel.Path}]  {channel.Kind?.ToString().ToLowerInvariant() ?? "not located"}");
        sb.Append(channel.BlockAt >= 0 ? $"  block @0x{channel.BlockAt:X}" : "");
        sb.Append(channel.CompiledAt is { } c ? $"  compiled copy @0x{c:X}" : channel.Compiled == "no compiled copy" ? "  (no compiled copy: evaluated from the section)" : $"  compiled: {channel.Compiled}");
        if (channel.Keys.Count == 0) sb.Append(channel.Default is { } d ? $"  default {ValueText(track, d)}, no keys" : "  no keys");
        foreach (var k in channel.Keys)
        {
            sb.AppendLine().Append(indent).Append($"  {S(k.Seconds),8} ({k.Frame,6})  {ValueText(track, k.Value),-28}{(k.Interp != null ? k.Interp.PadRight(9) : "")}");
            if (k.FrameAt >= 0) sb.Append($"  frame @0x{k.FrameAt:X} value @0x{k.ValueAt:X}");
            if (k.CompiledFrameAt >= 0) sb.Append($" | compiled @0x{k.CompiledFrameAt:X}/0x{k.CompiledValueAt:X}");
        }
        return sb.ToString();
    }

    private static bool IsNeutral(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && (name.StartsWith("Translation") || name.StartsWith("Shear") || name == "Angle" ? v == 0 : name.StartsWith("Scale") || name == "Opacity" ? v == 1 : false);

    /// <summary>A key value for comparing: numbers by value (-0.0 = 0.0 = 0), anything else as text.</summary>
    private static string Same(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Num(d) : value;

    public static string S(double seconds) => (double.IsNaN(seconds) ? "?" : Math.Round(seconds, 3).ToString("0.###", CultureInfo.InvariantCulture)) + " s";

    private static string Num(double v) => (v == 0 ? 0 : v).ToString("0.###", CultureInfo.InvariantCulture);
}
