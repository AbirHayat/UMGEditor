using System.Buffers.Binary;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Animation;

public enum ChannelKind { Float, Byte, Bool, Integer }

/// <summary>
/// Where one keyframe channel's keys are in the raw Zen chunk: the frame (int32) and value of every key, and the two
/// blocks that hold them (times: from the array header to the last frame; values: likewise), which identify the
/// compiled copy (the values block doesn't always follow the times block directly there).
/// </summary>
/// <param name="ValuesAt">Float: the FMovieSceneFloatValue (28 bytes: Value @0, tangents, InterpMode @24, TangentMode @25); Byte/Bool: 1 byte; Integer: 4</param>
public sealed record ChannelBytes(ChannelKind Kind, IReadOnlyList<int> FramesAt, IReadOnlyList<int> ValuesAt, (int Start, int End) Times, (int Start, int End) Values)
{
    public const int FloatValueSize = 28;
    public int ValueSize => Kind switch { ChannelKind.Float => FloatValueSize, ChannelKind.Integer => 4, _ => 1 };
    public int BlockStart => Times.Start;
}

/// <summary>Where the compiled copy (MovieSceneCompiledData) of a channel's keys is.</summary>
public sealed record CompiledKeys(IReadOnlyList<int> FramesAt, IReadOnlyList<int> ValuesAt)
{
    public int BlockStart => FramesAt.Count > 0 ? FramesAt[0] : -1;
}

/// <summary>
/// Finds keyframe channels in UE 4.26 movie scene sections by walking their tagged properties:
/// FMovieSceneFloatChannel is a native struct (PreInfinityExtrap, PostInfinityExtrap, then the bulk arrays
/// [int32 4][int32 n][frames…] and [int32 28][int32 n][FMovieSceneFloatValue…], then default, tick resolution…);
/// Byte/Bool/Integer channels are tagged structs whose Times is an ArrayProperty of FrameNumber structs and whose
/// Values is an ArrayProperty of ByteProperty/BoolProperty/IntProperty. Channel paths are tag names with "[i]" for
/// ArrayIndex > 0 (as AnimationReader names them: "BottomCurve", "Translation[1]").
/// </summary>
public static class AnimationBytes
{
    private static readonly Dictionary<string, ChannelKind> TaggedChannels = new()
    {
        ["MovieSceneByteChannel"] = ChannelKind.Byte,
        ["MovieSceneBoolChannel"] = ChannelKind.Bool,
        ["MovieSceneIntegerChannel"] = ChannelKind.Integer,
    };

    /// <summary>Every channel of a section export that can be located, by path.</summary>
    public static Dictionary<string, ChannelBytes> Locate(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, int sectionExport)
    {
        var result = new Dictionary<string, ChannelBytes>();
        var span = layout.SpanOf(sectionExport);
        foreach (var tag in Tags(chunk, layout, span.Start, span.End) ?? [])
        {
            if (tag.Type != "StructProperty") continue;
            var path = tag.ArrayIndex > 0 ? $"{tag.Name}[{tag.ArrayIndex}]" : tag.Name;
            var located = tag.StructName == "MovieSceneFloatChannel" ? FloatChannel(chunk, tag.ValueStart, tag.ValueStart + tag.Size)
                : TaggedChannels.TryGetValue(tag.StructName, out var kind) ? TaggedChannel(chunk, layout, tag.ValueStart, tag.ValueStart + tag.Size, kind)
                : null;
            if (located != null) result[path] = located;
        }
        return result;
    }

    /// <summary>
    /// The compiled copies (MovieSceneCompiledData) of channels, matched in the order the compiled data lists them: the
    /// first occurrence of the section's times block after the previous match, then of its values block after that
    /// (byte channels store DefaultValue/bHasDefaultValue in between there). Null where not found.
    /// </summary>
    public static List<CompiledKeys?> LocateInOrder(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, int compiledExport, IReadOnlyList<ChannelBytes> channels)
    {
        const int MaxGap = 256; // between a compiled copy's times and values blocks
        var span = layout.SpanOf(compiledExport);
        var data = chunk.Slice(span.Start, span.Size);
        var result = new List<CompiledKeys?>();
        int cursor = 0;
        foreach (var ch in channels)
        {
            var times = chunk[ch.Times.Start..ch.Times.End];
            var values = chunk[ch.Values.Start..ch.Values.End];
            var t = data[cursor..].IndexOf(times);
            if (t < 0) { result.Add(null); continue; }
            int tAt = cursor + t, vFrom = tAt + times.Length;
            var v = data.Slice(vFrom, Math.Min(MaxGap + values.Length, data.Length - vFrom)).IndexOf(values);
            if (v < 0) { result.Add(null); continue; }
            int timesShift = span.Start + tAt - ch.Times.Start, valuesShift = span.Start + vFrom + v - ch.Values.Start;
            result.Add(new CompiledKeys(ch.FramesAt.Select(f => f + timesShift).ToList(), ch.ValuesAt.Select(x => x + valuesShift).ToList()));
            cursor = vFrom + v + values.Length;
        }
        return result;
    }

    private static ChannelBytes? FloatChannel(ReadOnlySpan<byte> c, int start, int end)
    {
        int p = start + 2; // PreInfinityExtrap, PostInfinityExtrap
        if (p + 8 > end || I32(c, p) != 4) return null;
        var n = I32(c, p + 4);
        if (n < 0 || p + 8 + 4 * n + 8 > end) return null;
        var frames = Enumerable.Range(0, n).Select(k => p + 8 + 4 * k).ToList();
        int q = p + 8 + 4 * n;
        if (I32(c, q) != ChannelBytes.FloatValueSize || I32(c, q + 4) != n || q + 8 + ChannelBytes.FloatValueSize * n > end) return null;
        var values = Enumerable.Range(0, n).Select(k => q + 8 + ChannelBytes.FloatValueSize * k).ToList();
        return new ChannelBytes(ChannelKind.Float, frames, values, (p, q), (q, q + 8 + ChannelBytes.FloatValueSize * n));
    }

    private static ChannelBytes? TaggedChannel(ReadOnlySpan<byte> c, ZenPackageLayout layout, int start, int end, ChannelKind kind)
    {
        var tags = Tags(c, layout, start, end);
        if (tags?.FirstOrDefault(t => t.Name == "Times" && t.Type == "ArrayProperty") is not { Name: not null } times) return null;
        if (tags.FirstOrDefault(t => t.Name == "Values" && t.Type == "ArrayProperty") is not { Name: not null } values) return null;
        // Times: int32 count, then one inner StructProperty tag (Name, Type, Size, ArrayIndex, StructName, Guid, HasGuid = 49 bytes), then the frames
        var n = I32(c, times.ValueStart);
        int framesStart = times.ValueStart + 4 + 49;
        if (n < 0 || framesStart + 4 * n != times.ValueStart + times.Size) return null;
        var size = kind == ChannelKind.Integer ? 4 : 1;
        if (I32(c, values.ValueStart) != n || values.ValueStart + 4 + size * n != values.ValueStart + values.Size) return null;
        return new ChannelBytes(kind, Enumerable.Range(0, n).Select(k => framesStart + 4 * k).ToList(),
            Enumerable.Range(0, n).Select(k => values.ValueStart + 4 + size * k).ToList(),
            (times.ValueStart, times.ValueStart + times.Size), (values.ValueStart, values.ValueStart + values.Size));
    }

    // ---- tags ----

    private readonly record struct Tag(string Name, string Type, int Size, int ArrayIndex, string StructName, int ValueStart);

    private static List<Tag>? Tags(ReadOnlySpan<byte> c, ZenPackageLayout layout, int pos, int limit)
    {
        var tags = new List<Tag>();
        try
        {
            while (pos + 8 <= limit)
            {
                var name = Name(c, layout, pos); pos += 8;
                if (name == "None") return tags;
                var type = Name(c, layout, pos); pos += 8;
                var size = I32(c, pos); pos += 4;
                var arrayIndex = I32(c, pos); pos += 4;
                var structName = "";
                switch (type)
                {
                    case "StructProperty": structName = Name(c, layout, pos); pos += 8 + 16; break;
                    case "BoolProperty": pos += 1; break;
                    case "ByteProperty" or "EnumProperty" or "ArrayProperty" or "SetProperty": pos += 8; break;
                    case "MapProperty": pos += 16; break;
                }
                if (c[pos++] != 0) pos += 16;
                tags.Add(new Tag(name, type, size, arrayIndex, structName, pos));
                pos += size;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException) { }
        return null;
    }

    private static string Name(ReadOnlySpan<byte> c, ZenPackageLayout layout, int at)
    {
        var index = I32(c, at);
        var number = I32(c, at + 4);
        if (index < 0 || index >= layout.NameMap.Count) throw new InvalidDataException($"name index {index} at 0x{at:x}");
        return number == 0 ? layout.NameMap[index] : $"{layout.NameMap[index]}_{number - 1}";
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
}
