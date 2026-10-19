using System.Buffers.Binary;

namespace UmgEditor.Core.Patching;

public enum FieldType { Float, Int32, Bool }

/// <summary>A stored scalar inside an export: its dotted path (same naming as SlotField.Path) and exact byte position.</summary>
/// <param name="Offset">Absolute offset in the chunk of the value bytes (for Bool: the tag's BoolVal byte)</param>
public sealed record FieldLocation(string Path, int Offset, int Length, FieldType Type, byte[] Bytes)
{
    public double Value => Type switch
    {
        FieldType.Float => BinaryPrimitives.ReadSingleLittleEndian(Bytes),
        FieldType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(Bytes),
        _ => Bytes[0],
    };
}

/// <summary>
/// Walks UE 4.26 tagged property serialization (FPropertyTag) inside one export and reports where every float,
/// int32 and bool value lives. Tag: Name, Type (FNames = index into the Zen name map + number), Size, ArrayIndex,
/// type extras (StructName + Guid | BoolVal | EnumName | InnerType [+ ValueType]), HasPropertyGuid [+ Guid], value.
/// Tagged structs are walked recursively; known native structs (Vector2D etc.) are decoded by layout.
/// </summary>
public sealed class TaggedPropertyWalker(IReadOnlyList<string> nameMap)
{
    /// <summary>Native (binary) struct layouts this game's UI uses, as (member, float offset) lists.</summary>
    private static readonly Dictionary<string, string[]> NativeFloatStructs = new()
    {
        ["Vector2D"] = ["X", "Y"],
        ["Vector"] = ["X", "Y", "Z"],
        ["LinearColor"] = ["R", "G", "B", "A"],
        ["Vector4"] = ["X", "Y", "Z", "W"],
    };

    private static readonly HashSet<string> OpaqueNativeStructs = ["Guid", "Color", "IntPoint", "Rotator", "Quat", "Box2D", "SoftObjectPath", "FrameNumber", "DateTime", "Timespan"];

    public List<FieldLocation> Walk(ReadOnlySpan<byte> chunk, ExportSpan export)
    {
        var result = new List<FieldLocation>();
        var end = WalkTagged(chunk, export.Start, export.End, "", result);
        if (end > export.End) throw new InvalidDataException($"properties ran past the export end ({end} > {export.End})");
        return result;
    }

    /// <returns>Position after the terminating "None" tag.</returns>
    private int WalkTagged(ReadOnlySpan<byte> c, int pos, int limit, string prefix, List<FieldLocation> result)
    {
        while (true)
        {
            if (pos + 8 > limit) throw new InvalidDataException($"no terminating None tag before {limit} (at {prefix})");
            var name = Name(c, pos); pos += 8;
            if (name == "None") return pos;
            var type = Name(c, pos); pos += 8;
            var size = I32(c, pos); pos += 4;
            var arrayIndex = I32(c, pos); pos += 4;
            string? structName = null;
            int boolOffset = -1;
            switch (type)
            {
                case "StructProperty":
                    structName = Name(c, pos); pos += 8 + 16;
                    break;
                case "BoolProperty":
                    boolOffset = pos; pos += 1;
                    break;
                case "ByteProperty" or "EnumProperty" or "ArrayProperty" or "SetProperty":
                    pos += 8;
                    break;
                case "MapProperty":
                    pos += 16;
                    break;
            }
            var hasGuid = c[pos]; pos += 1;
            if (hasGuid != 0) pos += 16;

            var path = (prefix.Length == 0 ? name : prefix + "." + name) + (arrayIndex > 0 ? $"[{arrayIndex}]" : "");
            var valueStart = pos;
            if (valueStart + size > limit) throw new InvalidDataException($"{path}: value of {size} bytes runs past {limit}");

            switch (type)
            {
                case "FloatProperty" when size == 4:
                    result.Add(new FieldLocation(path, valueStart, 4, FieldType.Float, c.Slice(valueStart, 4).ToArray()));
                    break;
                case "IntProperty" when size == 4:
                    result.Add(new FieldLocation(path, valueStart, 4, FieldType.Int32, c.Slice(valueStart, 4).ToArray()));
                    break;
                case "BoolProperty":
                    result.Add(new FieldLocation(path, boolOffset, 1, FieldType.Bool, c.Slice(boolOffset, 1).ToArray()));
                    break;
                case "StructProperty" when structName != null && NativeFloatStructs.TryGetValue(structName, out var members) && size == members.Length * 4:
                    for (int m = 0; m < members.Length; m++)
                        result.Add(new FieldLocation(path + "." + members[m], valueStart + m * 4, 4, FieldType.Float, c.Slice(valueStart + m * 4, 4).ToArray()));
                    break;
                case "StructProperty" when structName != null && !OpaqueNativeStructs.Contains(structName):
                    // tagged struct: must end exactly at the declared size, otherwise it's native and we leave it alone
                    var sub = new List<FieldLocation>();
                    int subEnd;
                    try { subEnd = WalkTagged(c, valueStart, valueStart + size, path, sub); }
                    catch (InvalidDataException) { subEnd = -1; }
                    if (subEnd == valueStart + size) result.AddRange(sub);
                    break;
            }
            pos = valueStart + size;
        }
    }

    private string Name(ReadOnlySpan<byte> c, int at)
    {
        var index = I32(c, at);
        var number = I32(c, at + 4);
        if (index < 0 || index >= nameMap.Count) throw new InvalidDataException($"name index {index} out of range at {at}");
        return number == 0 ? nameMap[index] : $"{nameMap[index]}_{number - 1}";
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
}
