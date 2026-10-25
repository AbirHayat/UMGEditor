namespace UmgEditor.Core.Patching;

/// <summary>A renamed package chunk, or why it was refused, with what was checked.</summary>
public sealed record RenameResult(byte[]? Chunk, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log)
{
    public bool Ok => Chunk != null && Refusals.Count == 0;
}

/// <summary>
/// Same-length name-map renames of one package (plus optional same-size byte writes, e.g. a texture payload), checked
/// like every other patch: (1) the byte diff against stock is exactly the renamed string bytes, their hashes and the
/// planned writes; (2) every export re-parses, and each value that differs from stock is the stock value with the
/// renames applied (as substrings), nothing else. Branch experiment, canary C13 (soft-path re-points, cloned textures).
/// </summary>
public static class NameRenamePatch
{
    /// <param name="fileKey">The stock file the bytes come from (the parse context)</param>
    /// <param name="writes">Same-size writes at stock offsets (e.g. a bulk payload, or planned values: then list their parse change in expected)</param>
    /// <param name="expected">Values the writes change in the parse: (export, flattened path) → the new value; every other difference must be a rename</param>
    public static RenameResult Apply(GameSource source, string fileKey, byte[] stock, IReadOnlyList<(string From, string To)> renames,
        IReadOnlyList<(int Offset, byte[] Bytes, string What)>? writes = null, IReadOnlyDictionary<(int Export, string Path), string>? expected = null)
    {
        var log = new List<string>();
        var refusals = new List<string>();
        RenameResult Refuse(string why) { refusals.Add(why); return new RenameResult(null, refusals, log); }

        var (renamed, why) = ZenNameMap.Rename(stock, renames);
        if (renamed == null) return Refuse(why!);
        return Verify(source, fileKey, stock, renamed, renames, writes, expected);
    }

    /// <summary>
    /// The checks of <see cref="Apply"/> on a chunk renamed elsewhere (the planned writes are applied to it first):
    /// exact byte diff, then the re-parse against <see cref="RenameText"/> of each stock value. Also how a wrong rename
    /// is shown to be refused.
    /// </summary>
    public static RenameResult Verify(GameSource source, string fileKey, byte[] stock, byte[] renamedChunk, IReadOnlyList<(string From, string To)> renames,
        IReadOnlyList<(int Offset, byte[] Bytes, string What)>? writes = null, IReadOnlyDictionary<(int Export, string Path), string>? expected = null)
    {
        var log = new List<string>();
        var refusals = new List<string>();
        RenameResult Refuse(string why) { refusals.Add(why); return new RenameResult(null, refusals, log); }
        var renamed = (byte[]) renamedChunk.Clone();
        var names = ZenNameMap.Read(stock);
        var allowed = new HashSet<int>();
        foreach (var (from, to) in renames)
        {
            var n = names.Single(x => x.Text == from);
            for (int i = 0; i < n.ByteLength; i++) allowed.Add(n.StringAt + i);
            for (int i = 0; i < 8; i++) allowed.Add(n.HashAt + i);
            log.Add($"name {n.Index}: \"{from}\" → \"{to}\" (string @0x{n.StringAt:X}, hash @0x{n.HashAt:X} {n.Hash:x16} → {ZenNameMap.Hash(to, false):x16})");
        }
        foreach (var (offset, bytes, what) in writes ?? [])
        {
            if (offset < 0 || offset + bytes.Length > stock.Length) return Refuse($"{what}: 0x{offset:X}+{bytes.Length} is outside the chunk ({stock.Length} bytes)");
            if (Enumerable.Range(offset, bytes.Length).Any(allowed.Contains)) return Refuse($"{what}: overlaps a renamed name or its hash at 0x{offset:X}");
            bytes.CopyTo(renamed, offset);
            for (int i = 0; i < bytes.Length; i++) allowed.Add(offset + i);
            log.Add($"{what}: {bytes.Length} bytes @0x{offset:X}");
        }

        // 1. exact byte diff
        if (renamed.Length != stock.Length) return Refuse($"size changed: {stock.Length} → {renamed.Length}");
        var stray = Enumerable.Range(0, stock.Length).Where(i => stock[i] != renamed[i] && !allowed.Contains(i)).Take(5).ToList();
        if (stray.Count > 0) return Refuse($"bytes outside the plan differ: {string.Join(", ", stray.Select(i => $"0x{i:X}"))}");
        log.Add($"{Enumerable.Range(0, stock.Length).Count(i => stock[i] != renamed[i])} bytes differ from stock, size {stock.Length} unchanged");

        // 2. full re-parse: only renamed strings may differ, each exactly as RenameText predicts
        try
        {
            var before = source.ParsePackage(fileKey, stock);
            var after = source.ParsePackage(fileKey, renamed);
            if (before.ExportsLazy.Length != after.ExportsLazy.Length) return Refuse($"export count {before.ExportsLazy.Length} → {after.ExportsLazy.Length}");
            int changed = 0;
            var seen = new HashSet<(int, string)>();
            for (int i = 0; i < before.ExportsLazy.Length; i++)
            {
                var a = TagInserter.Flatten(before.ExportsLazy[i].Value);
                var b = TagInserter.Flatten(after.ExportsLazy[i].Value);
                foreach (var k in a.Keys.Union(b.Keys))
                {
                    var x = a.GetValueOrDefault(k);
                    var y = b.GetValueOrDefault(k);
                    if (x == y) continue;
                    if (x != null && y != null && y == RenameText(x, renames)) { changed++; continue; }
                    if (expected != null && expected.TryGetValue((i, k), out var want) && y != null && Same(y, want))
                    { changed++; seen.Add((i, k)); continue; }
                    refusals.Add($"re-parse: unintended difference in export {i} ({before.ExportsLazy[i].Value.Name}) {k}: {x ?? "(missing)"} → {y ?? "(missing)"}");
                }
            }
            foreach (var (key, want) in expected ?? new Dictionary<(int Export, string Path), string>())
                if (!seen.Contains(key)) refusals.Add($"re-parse: export {key.Export} {key.Path} doesn't read back as {want}");
            log.Add($"re-parse: {before.ExportsLazy.Length} exports, {changed} values differ, each only by the renames{(expected is { Count: > 0 } ? $" or the {expected.Count} planned values" : "")}");
        }
        catch (Exception e) { return Refuse($"re-parse failed: {e.GetType().Name}: {e.Message}"); }
        return refusals.Count > 0 ? new RenameResult(null, refusals, log) : new RenameResult(renamed, refusals, log);
    }

    /// <summary>
    /// A stock value as the renamed package parses it: one pass left to right; at each position the longest name that
    /// starts there is replaced, and the scan continues after the replacement, so replaced text is never renamed again
    /// (a texture named like its folder, /…/CmnIconAlert/Tex720/CmnIconAlert, keeps the folder). Ordinal.
    /// </summary>
    public static string RenameText(string s, IReadOnlyList<(string From, string To)> renames)
    {
        var byLength = renames.Where(r => r.From.Length > 0).OrderByDescending(r => r.From.Length).ToList();
        var o = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            var hit = byLength.FindIndex(r => s.AsSpan(i).StartsWith(r.From, StringComparison.Ordinal));
            if (hit < 0) { o.Append(s[i++]); continue; }
            o.Append(byLength[hit].To);
            i += byLength[hit].From.Length;
        }
        return o.ToString();
    }

    /// <summary>A parsed value equals the planned one: as floats when both are numbers, else as text (quotes ignored).</summary>
    private static bool Same(string parsed, string want)
    {
        var p = parsed.Trim('"');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return double.TryParse(p, System.Globalization.NumberStyles.Float, ci, out var a) && double.TryParse(want, System.Globalization.NumberStyles.Float, ci, out var b)
            ? (float) a == (float) b : p == want;
    }
}
