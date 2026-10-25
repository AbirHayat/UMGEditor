using System.Buffers.Binary;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Modding;

/// <summary>
/// Points chosen widgets' soft object paths (a uiImage's <c>texture</c>) at a new path without renaming the old one
/// (branch experiment, step 3 fix): the new path is appended to the name map (<see cref="EnumTagInsert.AppendNames"/>,
/// as C16b) unless it is already there, then each chosen export's stored FSoftObjectPath gets the new FName in place.
/// The struct is cooked UE 4.26: a top-level StructProperty "texture" of type SoftObjectPath, 12 bytes = AssetPathName
/// (name index + number) and an empty SubPathString (int32 0); only the 8 name bytes change. So the new path can have
/// any length, and other widgets naming the old path keep it. Checks: the append's own (old names and hashes kept,
/// header moved as one piece, every export re-parses identically), then on the appended chunk the exact byte diff and
/// the re-parse (<see cref="NameRenamePatch.Apply"/> without renames): only the chosen exports' path and the planned
/// values may differ.
/// </summary>
public static class SoftPathPoint
{
    public const string Property = "texture";

    /// <param name="fromObjectPath">The path the exports name now, e.g. "/Game/…/Tex720/X_1.X_1"</param>
    /// <param name="toObjectPath">The new path, e.g. "/Game/…/Tex720/Y_1.Y_1"</param>
    /// <param name="exports">The exports to point (each must name <paramref name="fromObjectPath"/>)</param>
    /// <param name="values">Stored values to change too (as <see cref="SoftPathRepoint.Build"/>)</param>
    /// <param name="input">The package bytes to start from (default: stock)</param>
    public static SoftPathRepoint.Result Build(GameSource source, string packagePath, string fromObjectPath, string toObjectPath, IReadOnlyList<string> exports,
        IReadOnlyList<(string Export, string Path, double Value)>? values = null, byte[]? input = null)
    {
        var (fileKey, stockBytes) = source.ReadPackageBytes(packagePath);
        var chunk = input ?? stockBytes;
        var log = new List<string>();
        SoftPathRepoint.Result Refuse(string why) => new(null, [why], log);
        if (exports.Count == 0 || exports.Distinct(StringComparer.Ordinal).Count() != exports.Count) return Refuse("the exports to point must be distinct and at least one");
        var (fromName, fromNumber) = Split(fromObjectPath);
        var (toName, toNumber) = Split(toObjectPath);
        if (fromName == toName && fromNumber == toNumber) return Refuse($"{toObjectPath} is the path already named");

        // 1. the new name (appended unless already there)
        var names = ZenNameMap.Read(chunk);
        if (names.Count(n => n.Text == toName) > 1) return Refuse($"\"{toName}\" occurs more than once in the name map");
        if (!names.Any(n => n.Text == toName))
        {
            var appended = EnumTagInsert.AppendNames(source, fileKey, chunk, [toName]);
            log.AddRange(appended.Log);
            if (appended.Chunk == null) return new SoftPathRepoint.Result(null, appended.Refusals, log);
            chunk = appended.Chunk;
        }
        else log.Add($"name \"{toName}\" is already in the name map");

        // 2. the 8 name bytes of each export's path, located by the tag walk on the (appended) chunk
        var parsed = source.ParsePackage(fileKey, chunk);
        var layout = ZenPackageLayout.Read(chunk, parsed);
        var exportNames = parsed.ExportsLazy.Select(e => e.Value.Name).ToList();
        int toIndex = layout.NameMap.ToList().IndexOf(toName);
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var writes = new List<(int Offset, byte[] Bytes, string What)>();
        var expected = new Dictionary<(int Export, string Path), string>();
        foreach (var export in exports)
        {
            if (exportNames.Count(e => e == export) != 1) return Refuse($"{exportNames.Count(e => e == export)} exports named {export}");
            var index = exportNames.IndexOf(export);
            List<TagLocation> tags;
            try { tags = walker.TopLevelTags(chunk, layout.SpanOf(index)); }
            catch (InvalidDataException e) { return Refuse($"{export}: the tags can't be walked ({e.Message})"); }
            if (tags.Where(t => t.Name == Property).ToList() is not [{ Type: "StructProperty", Extra: "SoftObjectPath", Size: 12 } tag])
                return Refuse($"{export}.{Property} isn't stored once as a 12-byte SoftObjectPath struct");
            var at = tag.ValueStart;
            int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(at)), number = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(at + 4));
            if (nameIndex < 0 || nameIndex >= layout.NameMap.Count || layout.NameMap[nameIndex] != fromName || number != fromNumber)
                return Refuse($"{export}.{Property} names {(nameIndex >= 0 && nameIndex < layout.NameMap.Count ? layout.NameMap[nameIndex] : $"name {nameIndex}")} #{number}, not {fromObjectPath}");
            if (BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(at + 8)) != 0) return Refuse($"{export}.{Property} has a SubPathString (only a plain asset path is pointed)");
            var window = BytePlanner.UniqueWindow(chunk, at, 8);
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, toIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), toNumber);
            writes.Add((at, bytes, $"{export}.{Property} → name {toIndex} #{toNumber}{(window is { } w ? $" (unique window ±{w})" : "")}"));
            expected[(index, $"Properties.{Property}.AssetPathName")] = toObjectPath;
        }
        if (SoftPathRepoint.PlanValues(source, packagePath, fileKey, chunk, values, writes, expected) is { } why) return Refuse(why);

        // 3. exact byte diff + re-parse against the appended chunk: only the planned paths and values differ
        var r = NameRenamePatch.Apply(source, fileKey, chunk, [], writes, expected);
        log.AddRange(r.Log);
        return r.Ok ? new SoftPathRepoint.Result(new ModPackage(packagePath, fileKey, source.ChunkIdHex(fileKey), r.Chunk!), [], log) : new SoftPathRepoint.Result(null, r.Refusals, log);
    }

    /// <summary>"…/X_1.X_1" → ("…/X_1.X", 2): the name-map string and the FName number (0 = none, n + 1 for "_n").</summary>
    public static (string Name, int Number) Split(string objectPath)
    {
        var name = TextureUsage.NameMapForm(objectPath);
        var suffix = objectPath[name.Length..];
        return (name, suffix.Length == 0 ? 0 : int.Parse(suffix[1..], System.Globalization.CultureInfo.InvariantCulture) + 1);
    }
}
