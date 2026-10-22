using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>
/// Re-points a soft object path (e.g. a uiImage's texture) in one stock package to another object of the same path
/// length: the path is a name in the Zen name map, so this is a same-length name rename (NameRenamePatch: exact byte
/// diff, and the re-parse may differ only by the rename). Branch experiment, canary C13. Step 3 (texture edits): the
/// input can be an earlier layer of the package (e.g. its saved edit-set chunk) instead of stock.
/// </summary>
public static class SoftPathRepoint
{
    public sealed record Result(ModPackage? Package, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    /// <param name="from">The stock object path, e.g. "/Game/…/Tex720/DemoTitlePlate.DemoTitlePlate"</param>
    /// <param name="values">Stored values to change too (export name, path, new value), e.g. Img_Plate's Margin.Left → 0:
    /// planned by BytePlanner (tag walk, stock value, location proof) and required to read back</param>
    /// <param name="input">The package bytes to start from (default: stock); values are planned and checked on these</param>
    public static Result Build(GameSource source, string packagePath, string from, string to, IReadOnlyList<(string Export, string Path, double Value)>? values = null, byte[]? input = null)
    {
        var (fileKey, stockBytes) = source.ReadPackageBytes(packagePath);
        var stock = input ?? stockBytes;
        var log = new List<string>();
        var writes = new List<(int Offset, byte[] Bytes, string What)>();
        var expected = new Dictionary<(int Export, string Path), string>();
        if (values is { Count: > 0 })
        {
            var package = source.ParsePackage(fileKey, stock);
            var exports = package.ExportsLazy.Select(e => e.Value).ToList();
            var changes = new List<FieldChange>();
            foreach (var (exportName, path, value) in values)
            {
                var index = exports.FindIndex(e => e.Name == exportName);
                if (index < 0 || exports.Count(e => e.Name == exportName) != 1) return new Result(null, [$"{packagePath} has {exports.Count(e => e.Name == exportName)} exports named {exportName}"], log);
                changes.Add(new FieldChange(new FieldKey(packagePath, index, path), 0, value));
                expected[(index, "Properties." + path)] = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            // the parse writes a float as its shortest decimal ("0.33"); the stored value is the float that decimal stands for
            double? Stock(FieldKey k) => WidgetTreeReader.StoredValues(exports[k.SlotExport]).TryGetValue(k.Path, out var t)
                && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                    ? t.IndexOfAny(['.', 'E', 'e']) >= 0 ? (float) v : v : null;
            var plan = BytePlanner.Plan(stock, ZenPackageLayout.Read(stock, package), packagePath, EditSet.Empty.With(changes, Stock), Stock, i => exports[i].Name);
            if (!plan.Ok) return new Result(null, plan.Refusals.Select(r => r.Message).ToList(), log);
            if (plan.Insertions.Count > 0) return new Result(null, ["only stored values can be changed here"], log);
            writes.AddRange(plan.Entries.Select(e => (e.Offset, e.NewBytes, $"{e.SlotName}.{e.Key.Path} {e.OldValue} → {e.NewValue} (context x{e.ContextMatches}, window ±{e.Window})")));
        }
        var r = NameRenamePatch.Apply(source, fileKey, stock, [(from, to)], writes, expected);
        log.AddRange(r.Log);
        return r.Ok ? new Result(new ModPackage(packagePath, fileKey, source.ChunkIdHex(fileKey), r.Chunk!), [], log) : new Result(null, r.Refusals, log);
    }
}
