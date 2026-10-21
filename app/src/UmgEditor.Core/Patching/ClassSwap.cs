using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;

namespace UmgEditor.Core.Patching;

/// <summary>
/// Changes the class of one export in a UE 4.26 Zen package to a class from another stock package (branch experiment,
/// canary C16: the goal banner's plate MatchTimeBase_1_C → DemoTitleBase_C, the replay plate DemoTitleBase_C →
/// MatchTimeBase_C). In 4.26 a package import's 8 bytes are the target export's GlobalImportIndex, which the target
/// package stores in its own export map (CUE4Parse resolves imports the same way), so no hash is computed: the new
/// class's and CDO's stored values are copied. Changed, all same size: the two import-map entries, the export's
/// ClassIndex and TemplateIndex, and the imported package id in the graph data. The container header's store entry must
/// get the same package swap (<see cref="Result.PackageSwap"/> → ModPackage.ImportedPackageSwaps). Refused when the
/// old class or CDO is referenced by anything else, when the old package provides any other import, or when the new
/// package is already imported. Checks: the byte diff is exactly those slots; the re-parse resolves the export's class
/// and template to the new ones and nothing else differs.
/// </summary>
public static class ClassSwap
{
    public sealed record Result(byte[]? Chunk, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log)
    {
        /// <summary>The imported package id swap (old → new) the store entry needs.</summary>
        public (ulong Old, ulong New) PackageSwap { get; init; }
    }

    private const int EntrySize = ZenPackageLayout.ExportMapEntrySize;

    /// <param name="newClassPackage">The stock package of the new class, e.g. /Game/…/DemoTitle/DemoTitleBase (class "&lt;Short&gt;_C", CDO "Default__&lt;Short&gt;_C")</param>
    public static Result Swap(GameSource source, string fileKey, byte[] chunk, string exportName, string newClassPackage)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);

        var parsed = source.ParsePackage(fileKey, chunk);
        var names = parsed.ExportsLazy.Select(e => e.Value.Name).ToList();
        if (names.Count(n => n == exportName) != 1) return Refuse($"{names.Count(n => n == exportName)} exports named {exportName}");
        int target = names.IndexOf(exportName);
        int importMapAt = I32(chunk, 40), exportMapAt = I32(chunk, 44), graphAt = I32(chunk, 52), graphSize = I32(chunk, 56);
        int importCount = (exportMapAt - importMapAt) / 8;
        if (importCount != parsed.ImportMap.Length) return Refuse($"import map size: {importCount} slots, the parse has {parsed.ImportMap.Length}");
        var imports = Enumerable.Range(0, importCount).Select(i => U64(chunk, importMapAt + 8 * i)).ToList();
        int entry = exportMapAt + target * EntrySize;
        ulong oldClass = U64(chunk, entry + 32), oldTemplate = U64(chunk, entry + 48);

        // the graph data: imported packages (id, arcs)
        var graph = new List<(ulong Id, int At, int Length)>();
        int pos = graphAt, count = I32(chunk, pos); pos += 4;
        for (int i = 0; i < count; i++)
        {
            int start = pos;
            ulong id = U64(chunk, pos); pos += 8;
            int bundles = I32(chunk, pos); pos += 4 + 8 * bundles;
            graph.Add((id, start, pos - start));
        }
        if (pos != graphAt + graphSize) return Refuse($"the graph data ends at 0x{pos:X}, not 0x{graphAt + graphSize:X}");

        // which imported package holds the old class: the one with an export whose GlobalImportIndex is the class import
        IoPackage? oldPackage = null;
        foreach (var p in parsed.ImportedPackages.Value)
            if (p != null && p.ExportMap.Any(e => e.GlobalImportIndex.TypeAndId == oldClass)) { oldPackage = p; break; }
        if (oldPackage == null) return Refuse($"{exportName}'s class import 0x{oldClass:X16} isn't an export of any imported package");
        string Name(IoPackage p, ulong globalIndex) => p.ExportsLazy[Array.FindIndex(p.ExportMap, e => e.GlobalImportIndex.TypeAndId == globalIndex)].Value.Name;
        if (!oldPackage.ExportMap.Any(e => e.GlobalImportIndex.TypeAndId == oldTemplate)) return Refuse($"{exportName}'s template isn't from the class's package {oldPackage.Name}");
        var oldId = CUE4Parse.UE4.IO.Objects.FPackageId.FromName(oldPackage.Name).id;
        log.Add($"{exportName} (export {target}): class {Name(oldPackage, oldClass)} / template {Name(oldPackage, oldTemplate)} from {oldPackage.Name}");

        // the new class and CDO, as their package stores them
        var newPackage = source.LoadPackage(newClassPackage) as IoPackage;
        if (newPackage == null) return Refuse($"{newClassPackage} isn't an IoStore package");
        var shortName = newClassPackage[(newClassPackage.LastIndexOf('/') + 1)..];
        int newClassExport = newPackage.ExportsLazy.Select(e => e.Value.Name).ToList().IndexOf(shortName + "_C");
        int newCdoExport = newPackage.ExportsLazy.Select(e => e.Value.Name).ToList().IndexOf("Default__" + shortName + "_C");
        if (newClassExport < 0 || newCdoExport < 0) return Refuse($"{newClassPackage} has no {shortName}_C / Default__{shortName}_C");
        var oldClassObj = oldPackage.ExportsLazy[Array.FindIndex(oldPackage.ExportMap, e => e.GlobalImportIndex.TypeAndId == oldClass)].Value;
        var newClassObj = newPackage.ExportsLazy[newClassExport].Value;
        if (oldClassObj.ExportType != newClassObj.ExportType) return Refuse($"the old class is a {oldClassObj.ExportType}, the new one a {newClassObj.ExportType}");
        ulong newClass = newPackage.ExportMap[newClassExport].GlobalImportIndex.TypeAndId, newTemplate = newPackage.ExportMap[newCdoExport].GlobalImportIndex.TypeAndId;
        var newId = CUE4Parse.UE4.IO.Objects.FPackageId.FromName(newPackage.Name).id;
        log.Add($"new: {shortName}_C 0x{newClass:X16} / Default__{shortName}_C 0x{newTemplate:X16} from {newPackage.Name}");

        // references: the old class and CDO only in their import slots and in this export's Class/Template
        foreach (var (value, what) in new[] { (oldClass, "class"), (oldTemplate, "template") })
        {
            if (imports.Count(v => v == value) != 1) return Refuse($"the old {what} occurs {imports.Count(v => v == value)} times in the import map");
            for (int e = 0; e < parsed.ExportMap.Length; e++)
                for (int field = 24; field <= 48; field += 8)
                    if (U64(chunk, exportMapAt + e * EntrySize + field) == value && !(e == target && field == (what == "class" ? 32 : 48)))
                        return Refuse($"the old {what} is also referenced by export {e} ({names[e]}) at +{field}");
        }
        // (null import slots hold ~0, which is also every non-public export's GlobalImportIndex: not a reference)
        var oldPackageImports = imports.Where(v => v != ulong.MaxValue && oldPackage.ExportMap.Any(e => e.GlobalImportIndex.TypeAndId == v)).ToList();
        if (oldPackageImports.Count != 2)
            return Refuse($"{oldPackage.Name} provides {oldPackageImports.Count} imports (only its class and CDO may be swapped away): " +
                string.Join(", ", oldPackageImports.Select(v => $"[{imports.IndexOf(v)}] {Name(oldPackage, v)}")));
        if (graph.Count(g => g.Id == oldId) != 1) return Refuse($"{oldPackage.Name} isn't imported exactly once in the graph data");
        if (graph.Any(g => g.Id == newId)) return Refuse($"{newPackage.Name} is already imported (merging imports isn't supported)");
        if (imports.Contains(newClass) || imports.Contains(newTemplate)) return Refuse("the new class or CDO is already imported");

        // writes
        var result = chunk.ToArray();
        var planned = new List<(int At, int Length, string What)>();
        void W64(int at, ulong value, string what) { BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(at), value); planned.Add((at, 8, what)); }
        W64(importMapAt + 8 * imports.IndexOf(oldClass), newClass, "import: class");
        W64(importMapAt + 8 * imports.IndexOf(oldTemplate), newTemplate, "import: CDO");
        W64(entry + 32, newClass, $"export {target} ClassIndex");
        W64(entry + 48, newTemplate, $"export {target} TemplateIndex");
        W64(graph.Single(g => g.Id == oldId).At, newId, "graph: imported package id");
        // stock cooks the imported packages sorted by id: keep them so (the entries move as whole blocks, same size)
        var sortedInStock = graph.Select(g => g.Id).SequenceEqual(graph.Select(g => g.Id).Order());
        if (sortedInStock)
        {
            var entries = graph.Select(g => result.AsSpan(g.At, g.Length).ToArray()).ToList();
            var order = entries.OrderBy(e => BinaryPrimitives.ReadUInt64LittleEndian(e)).ToList();
            if (!order.SequenceEqual(entries))
            {
                int at = graphAt + 4;
                foreach (var e in order) { e.CopyTo(result.AsSpan(at)); at += e.Length; }
                planned.Add((graphAt + 4, graphSize - 4, "graph: entries re-sorted by package id, as stock keeps them"));
            }
        }
        var changed = Enumerable.Range(0, chunk.Length).Where(i => chunk[i] != result[i]).ToList();
        if (changed.Any(i => !planned.Any(p => i >= p.At && i < p.At + p.Length))) return Refuse("bytes outside the planned slots differ");
        log.Add($"{changed.Count} bytes in {planned.Count} slots: {string.Join(", ", planned.Select(p => $"{p.What} @0x{p.At:X}"))}");
        // the graph after: the same entries (one id swapped), sorted when stock was
        var after64 = new List<ulong>();
        for (int i = 0, p = graphAt + 4; i < count; i++) { after64.Add(U64(result, p)); p += 12 + 8 * I32(result, p + 8); }
        var expectedIds = graph.Select(g => g.Id == oldId ? newId : g.Id).ToList();
        if (!after64.Order().SequenceEqual(expectedIds.Order()) || (sortedInStock && !after64.SequenceEqual(after64.Order())))
            return Refuse($"the graph's imported packages after the swap ({string.Join(" ", after64.Select(x => x.ToString("x16")))}) aren't the planned set{(sortedInStock ? ", sorted" : "")}");
        log.Add($"graph imported packages: {string.Join(" ", after64.Select(x => x.ToString("x16")))}{(sortedInStock ? " (sorted, as stock)" : "")}");

        // the re-parse: this export's class and template are the new ones; nothing else differs
        var after = source.ParsePackage(fileKey, result);
        var swapped = after.ExportsLazy[target].Value;
        if (swapped.Class?.Name != shortName + "_C") return Refuse($"after the swap the export's class reads as {swapped.Class?.Name ?? "(unresolved)"}");
        // also allowed: the owning blueprint class's member variable named after the widget (an object property whose
        // PropertyClass is the same class import), which now has the new class's type, consistent with the instance
        var memberVars = 0;
        var flatBefore = new Dictionary<int, Dictionary<string, string>>();
        Dictionary<string, string> Before(int i) => flatBefore.TryGetValue(i, out var f) ? f : flatBefore[i] = TagInserter.Flatten(parsed.ExportsLazy[i].Value);
        var (_, problems, _) = TagInserter.ReparseDiff(source, fileKey, parsed, result, null, (export, key, old, now) =>
        {
            if (export == target) return key is "Type" or "Class" || key.StartsWith("Template.", StringComparison.Ordinal);
            // a reference to the swapped export shows its class: "OldClass_C'…:WidgetTree.<export>'" → "NewClass_C'…'"
            var oldClassName = oldClassObj.Name;
            if (key.EndsWith(".ObjectName", StringComparison.Ordinal) && old != null && now != null && old.Contains($"WidgetTree.{exportName}'", StringComparison.Ordinal)
                && now == old.Replace(oldClassName + "'", shortName + "_C'", StringComparison.Ordinal)) return true;
            var m =System.Text.RegularExpressions.Regex.Match(key, @"^ChildProperties\[(\d+)\]\.PropertyClass\.(ObjectName|ObjectPath)$");
            if (!m.Success || Before(export).GetValueOrDefault($"ChildProperties[{m.Groups[1].Value}].Name")?.Trim('"') != exportName) return false;
            if (now == null || !now.Contains(shortName + (m.Groups[2].Value == "ObjectName" ? "_C" : ""), StringComparison.Ordinal)) return false;
            memberVars++;
            return true;
        });
        if (problems.Count > 0) return new Result(null, problems, log);
        log.Add($"re-parse: {exportName} is now a {swapped.ExportType} ({swapped.Class?.Name}); only its class/template differ" +
            (memberVars > 0 ? $", and the blueprint's member variable {exportName} is now typed {shortName}_C" : ""));
        return new Result(result, [], log) { PackageSwap = (oldId, newId) };
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
    private static ulong U64(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadUInt64LittleEndian(c[at..]);
}
