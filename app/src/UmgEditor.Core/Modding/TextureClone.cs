using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO.Objects;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>
/// A new texture package made from a stock one (branch experiment, canary C13). The tutorial's rule: new pixels never go
/// under a stock texture package id, they go into a new package with a same-length new name. Here: same folder, the
/// short name renamed in the name map (package path and export name; same length), and the pixels of every inline
/// B8G8R8A8 mip replaced with exactly as many bytes. Mip 0 is located by its stock bytes (exactly once); the others by
/// their stored OffsetInFile relative to mip 0, and each must hold exactly its parsed bytes there. The new package id is FPackageId of the new path; the store entry
/// in the mod's container header is copied from the stock package (ModPackage.CloneOf).
/// </summary>
public static class TextureClone
{
    public sealed record Result(ModPackage? Package, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    /// <param name="pixelsFor">The new pixels of a mip (width, height → B, G, R, A per pixel); exactly the stock mip's size</param>
    public static Result Build(GameSource source, string stockPath, string newShortName, Func<int, int, byte[]> pixelsFor)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);

        var (fileKey, stock) = source.ReadPackageBytes(stockPath);
        var oldShortName = stockPath[(stockPath.LastIndexOf('/') + 1)..];
        var newPath = stockPath[..(stockPath.LastIndexOf('/') + 1)] + newShortName;
        if (newShortName.Length != oldShortName.Length) return Refuse($"\"{newShortName}\" must be as long as \"{oldShortName}\" ({oldShortName.Length} characters)");
        var newFileKey = fileKey[..^(oldShortName.Length + ".uasset".Length)] + newShortName + ".uasset";
        if (!fileKey.EndsWith("/" + oldShortName + ".uasset", StringComparison.Ordinal)) return Refuse($"unexpected file key {fileKey}");
        if (source.ChunkIdHex(newFileKey).Length > 0) return Refuse($"{newFileKey} already exists in the stock containers");

        // the stock texture: one Texture2D export, B8G8R8A8, every mip inline, each new mip exactly its stock size
        var package = source.ParsePackage(fileKey, stock);
        if (package.ExportsLazy.Length != 1 || package.ExportsLazy[0].Value is not UTexture2D texture) return Refuse($"{stockPath} isn't a package with one Texture2D export");
        var platform = texture.PlatformData;
        if (platform.PixelFormat != "PF_B8G8R8A8") return Refuse($"{stockPath} is {platform.PixelFormat}, not PF_B8G8R8A8");
        if (platform.Mips.Length == 0 || platform.Mips.Any(m => m.BulkData is not { } b || !b.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_ForceInlinePayload) || b.Data == null))
            return Refuse($"{stockPath}: every mip must have an inline payload");
        var mips = platform.Mips.Select(m => (m.SizeX, m.SizeY, Data: m.BulkData!.Data!, Offset: m.BulkData.Header.OffsetInFile)).ToList();
        var pixels = mips.Select(m => pixelsFor(m.SizeX, m.SizeY)).ToList();
        for (int k = 0; k < mips.Count; k++)
            if (pixels[k].Length != mips[k].Data.Length) return Refuse($"the new pixels of mip {k} are {pixels[k].Length} bytes, the stock mip {mips[k].Data.Length} ({mips[k].SizeX}×{mips[k].SizeY}×4)");
        var layout = ZenPackageLayout.Read(stock, package);
        var span = layout.SpanOf(0);
        var inExport = BytePlanner.CountOccurrences(stock.AsSpan(span.Start, span.Size), mips[0].Data);
        var inChunk = BytePlanner.CountOccurrences(stock, mips[0].Data);
        if (inExport != 1 || inChunk != 1) return Refuse($"mip 0's stock payload occurs {inExport} times in the export and {inChunk} times in the chunk (needs exactly once)");
        var mip0At = span.Start + stock.AsSpan(span.Start, span.Size).IndexOf(mips[0].Data);
        var writes = new List<(int Offset, byte[] Bytes, string What)>();
        for (int k = 0; k < mips.Count; k++)
        {
            var at = mip0At + (int) (mips[k].Offset - mips[0].Offset);
            if (at < span.Start || at + mips[k].Data.Length > span.End || !stock.AsSpan(at, mips[k].Data.Length).SequenceEqual(mips[k].Data))
                return Refuse($"mip {k} isn't where its OffsetInFile puts it relative to mip 0 (0x{at:X})");
            writes.Add((at, pixels[k], $"mip {k} payload ({mips[k].SizeX}×{mips[k].SizeY})"));
        }
        log.Add($"{stockPath}: {platform.SizeX}×{platform.SizeY} {platform.PixelFormat}, {mips.Count} inline mip{(mips.Count == 1 ? "" : "s")} @{string.Join(", ", writes.Select(w => $"0x{w.Offset:X}"))}");

        // every name holding the short name must be the package path or the export name
        if (NameRenames(stock, stockPath, newShortName, out var renames) is { } bad) return Refuse(bad);
        var renamed = NameRenamePatch.Apply(source, fileKey, stock, renames, writes);
        log.AddRange(renamed.Log);
        if (!renamed.Ok) return new Result(null, renamed.Refusals, log);

        // decode back: same size, the new name, exactly the new pixels
        var back = source.ParsePackage(fileKey, renamed.Chunk!);
        if (back.ExportsLazy[0].Value is not UTexture2D t2 || t2.Name != newShortName) return Refuse("the clone doesn't parse as a Texture2D with the new name");
        if (t2.PlatformData.SizeX != platform.SizeX || t2.PlatformData.SizeY != platform.SizeY || t2.PlatformData.Mips.Length != mips.Count
            || Enumerable.Range(0, mips.Count).Any(k => !(t2.PlatformData.Mips[k].BulkData?.Data ?? []).AsSpan().SequenceEqual(pixels[k])))
            return Refuse("the clone's mips don't decode to the new pixels");
        log.Add($"decoded back: {newShortName}, {t2.PlatformData.SizeX}×{t2.PlatformData.SizeY}, every mip = the new pixels");

        var idBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, FPackageId.FromName(newPath).id);
        var chunkId = Convert.ToHexString(idBytes).ToLowerInvariant() + "00000002";
        log.Add($"new package {newPath}: id {FPackageId.FromName(newPath).id:x16}, chunk {chunkId}, file {newFileKey}");
        return new Result(new ModPackage(newPath, newFileKey, chunkId, renamed.Chunk!) { CloneOf = fileKey }, [], log);
    }

    /// <summary>One mip of a resized clone: its size and pixels (B, G, R, A per pixel, width × height × 4 bytes).</summary>
    public sealed record Mip(int Width, int Height, byte[] Bgra);

    /// <summary>
    /// A new texture package from a stock one with mips of a different size (branch experiment, canary C14): the rename
    /// as in <see cref="Build"/>, then every inline mip replaced by a payload of any size. The cooked platform data is
    /// walked from its "PF_B8G8R8A8" string and every stock field is checked against the parse before anything moves:
    /// SizeX/SizeY/PackedData, FirstMip/NumMips, per mip bCooked/flags/ElementCount/SizeOnDisk/OffsetInFile/payload/
    /// SizeX/SizeY/SizeZ, bIsVirtual, and SkipOffset (the end of the platform data in legacy offsets, consistent with
    /// mip 0's OffsetInFile). Rewritten: the texture size, each mip's counts and size, the later mips' OffsetInFile,
    /// SkipOffset, ImportedSize, and the export's CookedSerialSize. Checks: with the payloads cut out, the result
    /// differs from the renamed stock only in those fields; the re-parse differs only in them, the renames, and the
    /// pixels, which decode back exactly. Same mip count as stock.
    /// </summary>
    public static Result BuildResized(GameSource source, string stockPath, string newShortName, IReadOnlyList<Mip> newMips)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);

        var (fileKey, stock) = source.ReadPackageBytes(stockPath);
        var oldShortName = stockPath[(stockPath.LastIndexOf('/') + 1)..];
        var newPath = stockPath[..(stockPath.LastIndexOf('/') + 1)] + newShortName;
        if (newShortName.Length != oldShortName.Length) return Refuse($"\"{newShortName}\" must be as long as \"{oldShortName}\" ({oldShortName.Length} characters)");
        if (!fileKey.EndsWith("/" + oldShortName + ".uasset", StringComparison.Ordinal)) return Refuse($"unexpected file key {fileKey}");
        var newFileKey = fileKey[..^(oldShortName.Length + ".uasset".Length)] + newShortName + ".uasset";
        if (source.ChunkIdHex(newFileKey).Length > 0) return Refuse($"{newFileKey} already exists in the stock containers");

        var package = source.ParsePackage(fileKey, stock);
        if (package.ExportsLazy.Length != 1 || package.ExportsLazy[0].Value is not UTexture2D texture) return Refuse($"{stockPath} isn't a package with one Texture2D export");
        var platform = texture.PlatformData;
        if (platform.PixelFormat != "PF_B8G8R8A8") return Refuse($"{stockPath} is {platform.PixelFormat}, not PF_B8G8R8A8");
        if (platform.Mips.Length == 0 || platform.Mips.Any(m => m.BulkData is not { } b || !b.BulkDataFlags.HasFlag(EBulkDataFlags.BULKDATA_ForceInlinePayload) || b.Data == null))
            return Refuse($"{stockPath}: every mip must have an inline payload");
        if (newMips.Count != platform.Mips.Length) return Refuse($"{newMips.Count} new mips, the stock texture has {platform.Mips.Length} (the mip count stays)");
        for (int k = 0; k < newMips.Count; k++)
        {
            if (newMips[k].Width <= 0 || newMips[k].Height <= 0 || newMips[k].Bgra.Length != newMips[k].Width * newMips[k].Height * 4)
                return Refuse($"new mip {k}: {newMips[k].Bgra.Length} bytes for {newMips[k].Width}×{newMips[k].Height} (needs width × height × 4)");
            if (k > 0 && (newMips[k].Width != Math.Max(1, newMips[k - 1].Width / 2) || newMips[k].Height != Math.Max(1, newMips[k - 1].Height / 2)))
                return Refuse($"new mip {k} is {newMips[k].Width}×{newMips[k].Height}; a mip chain halves each step");
        }

        // rename first (verified on its own), then move on the renamed bytes: the rename keeps every offset
        if (NameRenames(stock, stockPath, newShortName, out var renames) is { } bad) return Refuse(bad);
        var renamed = NameRenamePatch.Apply(source, fileKey, stock, renames);
        log.AddRange(renamed.Log);
        if (!renamed.Ok) return new Result(null, renamed.Refusals, log);
        var r = renamed.Chunk!;

        // walk the cooked platform data
        var layout = ZenPackageLayout.Read(stock, package);
        var span = layout.SpanOf(0);
        if (span.End != stock.Length) return Refuse($"the texture export ends at 0x{span.End:X}, the chunk at 0x{stock.Length:X} (expected the export to be last)");
        byte[] pfString = [0x0C, 0, 0, 0, .. "PF_B8G8R8A8"u8, 0];
        var exportBytes = r.AsSpan(span.Start, span.Size);
        if (BytePlanner.CountOccurrences(exportBytes, pfString) != 1) return Refuse("the \"PF_B8G8R8A8\" platform data string isn't in the export exactly once");
        int pf = span.Start + exportBytes.IndexOf(pfString);
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(at));
        long I64(int at) => BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(at));
        int sizeXAt = pf - 12, sizeYAt = pf - 8, skipAt = pf - 20;
        if (I32(sizeXAt) != platform.SizeX || I32(sizeYAt) != platform.SizeY || BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(pf - 4)) != platform.PackedData)
            return Refuse($"SizeX/SizeY/PackedData before the format string don't match the parse ({platform.SizeX}×{platform.SizeY})");
        int p = pf + pfString.Length;
        if (I32(p) != platform.FirstMipToSerialize || I32(p + 4) != platform.Mips.Length) return Refuse("FirstMipToSerialize/NumMips don't match the parse");
        p += 8;
        var stockMips = new List<(int HeaderAt, int PayloadAt, int Length, long OffsetInFile)>();
        for (int k = 0; k < platform.Mips.Length; k++)
        {
            var m = platform.Mips[k];
            var data = m.BulkData!.Data!;
            if (I32(p) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(p + 4)) != (uint) m.BulkData.BulkDataFlags || I32(p + 8) != data.Length || I32(p + 12) != data.Length
                || !r.AsSpan(p + 24, data.Length).SequenceEqual(data))
                return Refuse($"mip {k}'s cooked header or payload at 0x{p:X} doesn't match the parse (bCooked, flags, ElementCount and SizeOnDisk as int32, then the payload)");
            int after = p + 24 + data.Length;
            if (I32(after) != m.SizeX || I32(after + 4) != m.SizeY || I32(after + 8) != m.SizeZ) return Refuse($"mip {k}'s SizeX/SizeY/SizeZ don't match the parse");
            stockMips.Add((p, p + 24, data.Length, I64(p + 16)));
            // the stored OffsetInFile is relative (CUE4Parse reports it plus a base): both must move together across mips
            if (k > 0 && (stockMips[k].OffsetInFile - stockMips[k].PayloadAt != stockMips[0].OffsetInFile - stockMips[0].PayloadAt
                          || m.BulkData.Header.OffsetInFile - stockMips[k].OffsetInFile != platform.Mips[0].BulkData!.Header.OffsetInFile - stockMips[0].OffsetInFile))
                return Refuse($"mip {k}'s OffsetInFile isn't consistent with mip 0's");
            p = after + 12;
        }
        if (I32(p) != 0) return Refuse($"bIsVirtual at 0x{p:X} isn't 0");
        int platformEnd = p + 4;
        long legacyDelta = stockMips[0].OffsetInFile - stockMips[0].PayloadAt;
        if (I64(skipAt) != platformEnd + legacyDelta) return Refuse($"SkipOffset {I64(skipAt)} isn't the end of the platform data ({platformEnd} + {legacyDelta})");
        log.Add($"platform data: {platform.SizeX}×{platform.SizeY}, {stockMips.Count} mip{(stockMips.Count == 1 ? "" : "s")} @{string.Join(", ", stockMips.Select(m => $"0x{m.PayloadAt:X}+{m.Length}"))}, SkipOffset {I64(skipAt)}, legacy offsets = chunk + {legacyDelta}");

        // ImportedSize (a tagged IntPoint): located by its tag, checked against the parse
        int importedAt = -1;
        var names = ZenNameMap.Read(r);
        int NameIndex(string n) => names.FirstOrDefault(x => x.Text == n)?.Index ?? -1;
        if (texture.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FIntPoint?>("ImportedSize") is { } imported)
        {
            // the whole tag header: Name, Type StructProperty, Size 8, ArrayIndex 0, StructName IntPoint (FNames as index + number 0)
            var tagHeader = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(tagHeader, NameIndex("ImportedSize"));
            BinaryPrimitives.WriteInt32LittleEndian(tagHeader.AsSpan(8), NameIndex("StructProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tagHeader.AsSpan(16), 8);
            BinaryPrimitives.WriteInt32LittleEndian(tagHeader.AsSpan(24), NameIndex("IntPoint"));
            var hits = BytePlanner.CountOccurrences(r.AsSpan(span.Start, pf - span.Start), tagHeader);
            if (hits != 1) return Refuse($"the ImportedSize tag occurs {hits} times before the platform data");
            int tag = span.Start + r.AsSpan(span.Start, pf - span.Start).IndexOf(tagHeader);
            if (r[tag + 48] != 0 || I32(tag + 49) != imported.X || I32(tag + 53) != imported.Y)
                return Refuse("the ImportedSize tag isn't a plain IntPoint holding the parsed value");
            importedAt = tag + 49;
        }

        // splice: stock bytes with the new payloads, then the planned fields
        var output = new List<byte>(r.Length + newMips.Sum(m => m.Bgra.Length));
        int cursor = 0;
        var fieldAt = new List<(int Offset, int Size, string What)>(); // positions in the output
        int shift = 0;
        var newOffsetInFile = new long[stockMips.Count];
        for (int k = 0; k < stockMips.Count; k++)
        {
            output.AddRange(r.AsSpan(cursor, stockMips[k].PayloadAt - cursor).ToArray());
            newOffsetInFile[k] = stockMips[k].OffsetInFile + shift;
            output.AddRange(newMips[k].Bgra);
            cursor = stockMips[k].PayloadAt + stockMips[k].Length;
            shift += newMips[k].Bgra.Length - stockMips[k].Length;
        }
        output.AddRange(r.AsSpan(cursor).ToArray());
        var o = output.ToArray();
        int Out(int stockOffset) // a stock offset → its place in the output (only for offsets outside the payloads)
        {
            int s = 0;
            for (int k = 0; k < stockMips.Count; k++) if (stockOffset >= stockMips[k].PayloadAt + stockMips[k].Length) s += newMips[k].Bgra.Length - stockMips[k].Length;
            return stockOffset + s;
        }
        void W32(int stockOffset, int value, string what) { BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(Out(stockOffset)), value); fieldAt.Add((Out(stockOffset), 4, what)); }
        void W64(int stockOffset, long value, string what) { BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(Out(stockOffset)), value); fieldAt.Add((Out(stockOffset), 8, what)); }
        W32(sizeXAt, newMips[0].Width, "SizeX");
        W32(sizeYAt, newMips[0].Height, "SizeY");
        W64(skipAt, I64(skipAt) + shift, "SkipOffset");
        if (importedAt >= 0) { W32(importedAt, newMips[0].Width, "ImportedSize.X"); W32(importedAt + 4, newMips[0].Height, "ImportedSize.Y"); }
        for (int k = 0; k < stockMips.Count; k++)
        {
            int h = stockMips[k].HeaderAt, after = stockMips[k].PayloadAt + stockMips[k].Length;
            W32(h + 8, newMips[k].Bgra.Length, $"mip {k} ElementCount");
            W32(h + 12, newMips[k].Bgra.Length, $"mip {k} SizeOnDisk");
            W64(h + 16, newOffsetInFile[k], $"mip {k} OffsetInFile");
            W32(after, newMips[k].Width, $"mip {k} SizeX");
            W32(after + 4, newMips[k].Height, $"mip {k} SizeY");
        }
        var serialSizeAt = layout.ExportMapOffset + 8; // CookedSerialSize of export 0 (uint64)
        var serialSize = BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(serialSizeAt));
        if (serialSize != (ulong) span.Size) return Refuse($"CookedSerialSize {serialSize} isn't the export's size {span.Size}");
        BinaryPrimitives.WriteUInt64LittleEndian(o.AsSpan(serialSizeAt), serialSize + (ulong) shift);
        fieldAt.Add((serialSizeAt, 8, "CookedSerialSize"));
        log.Add($"resized: {platform.SizeX}×{platform.SizeY} → {newMips[0].Width}×{newMips[0].Height}, chunk {stock.Length} → {o.Length} bytes ({(shift >= 0 ? "+" : "")}{shift}); fields: {string.Join(", ", fieldAt.Select(f => f.What))}");

        // check 1: payloads cut out, only the planned fields differ from the renamed stock
        var planned = fieldAt.SelectMany(f => Enumerable.Range(f.Offset, f.Size)).ToHashSet();
        var cutOut = new List<(int Offset, int Length)>();
        { int s = 0; for (int k = 0; k < stockMips.Count; k++) { cutOut.Add((stockMips[k].PayloadAt + s, newMips[k].Bgra.Length)); s += newMips[k].Bgra.Length - stockMips[k].Length; } }
        var keptOut = Enumerable.Range(0, o.Length).Where(i => !cutOut.Any(c => i >= c.Offset && i < c.Offset + c.Length)).ToList();
        var keptStock = Enumerable.Range(0, r.Length).Where(i => !stockMips.Any(m => i >= m.PayloadAt && i < m.PayloadAt + m.Length)).ToList();
        if (keptOut.Count != keptStock.Count) return Refuse("with the payloads cut out, the result and the stock differ in length");
        var stray = keptOut.Zip(keptStock).Where(x => o[x.First] != r[x.Second] && !planned.Contains(x.First)).Take(5).ToList();
        if (stray.Count > 0) return Refuse($"bytes outside the planned fields differ: {string.Join(", ", stray.Select(x => $"0x{x.First:X}"))}");

        // check 2: re-parse; only the planned values, the renames and the pixels differ
        var back = source.ParsePackage(fileKey, o);
        if (back.ExportsLazy[0].Value is not UTexture2D t2 || t2.Name != newShortName) return Refuse("the clone doesn't parse as a Texture2D with the new name");
        if (t2.PlatformData.SizeX != newMips[0].Width || t2.PlatformData.SizeY != newMips[0].Height || t2.PlatformData.Mips.Length != newMips.Count
            || Enumerable.Range(0, newMips.Count).Any(k => t2.PlatformData.Mips[k].SizeX != newMips[k].Width || t2.PlatformData.Mips[k].SizeY != newMips[k].Height
                || !(t2.PlatformData.Mips[k].BulkData?.Data ?? []).AsSpan().SequenceEqual(newMips[k].Bgra)))
            return Refuse("the clone's mips don't decode to the new sizes and pixels");
        var a = TagInserter.Flatten(package.ExportsLazy[0].Value);
        var b = TagInserter.Flatten(t2);
        string[] allowed = ["SizeX", "SizeY", "Properties.ImportedSize.X", "Properties.ImportedSize.Y"];
        foreach (var key in a.Keys.Union(b.Keys))
        {
            var x = a.GetValueOrDefault(key); var y = b.GetValueOrDefault(key);
            if (x == y || (x != null && y != null && y == x.Replace(stockPath, newPath).Replace(oldShortName, newShortName))) continue;
            if (allowed.Contains(key) || (key.StartsWith("Mips[", StringComparison.Ordinal) && (key.EndsWith(".SizeX") || key.EndsWith(".SizeY") || key.Contains(".BulkData.")))) continue;
            return Refuse($"re-parse: unintended difference {key}: {x ?? "(missing)"} → {y ?? "(missing)"}");
        }
        log.Add($"decoded back: {newShortName}, {t2.PlatformData.SizeX}×{t2.PlatformData.SizeY}, {newMips.Count} mip{(newMips.Count == 1 ? "" : "s")} = the new pixels; re-parse differs only in sizes, mip headers and the renames");

        var idBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, FPackageId.FromName(newPath).id);
        var chunkId = Convert.ToHexString(idBytes).ToLowerInvariant() + "00000002";
        log.Add($"new package {newPath}: id {FPackageId.FromName(newPath).id:x16}, chunk {chunkId}, file {newFileKey}");
        return new Result(new ModPackage(newPath, newFileKey, chunkId, o) { CloneOf = fileKey }, [], log);
    }

    /// <summary>The next mip: half the width and height, each pixel the average of its 2×2 source block (B, G, R, A).</summary>
    public static Mip Half(Mip m)
    {
        int w = Math.Max(1, m.Width / 2), h = Math.Max(1, m.Height / 2);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(m.Width - 1, 2 * x + dx), sy = Math.Min(m.Height - 1, 2 * y + dy);
                            sum += m.Bgra[(sy * m.Width + sx) * 4 + c]; n++;
                        }
                    o[(y * w + x) * 4 + c] = (byte) ((sum + n / 2) / n);
                }
        return new Mip(w, h, o);
    }

    /// <summary>
    /// FName keeps a trailing "_N" (digits, no leading zero unless just "0") as a number, so the name map holds only the
    /// part before it: "MatchTimePlate_0" is "MatchTimePlate" with number 1. Returns (base, suffix).
    /// </summary>
    public static (string Base, string Suffix) SplitNumber(string name)
    {
        var i = name.LastIndexOf('_');
        if (i <= 0 || i == name.Length - 1) return (name, "");
        var digits = name[(i + 1)..];
        return digits.All(char.IsAsciiDigit) && (digits[0] != '0' || digits.Length == 1) ? (name[..i], name[i..]) : (name, "");
    }

    /// <summary>
    /// Why <see cref="Build"/>/<see cref="BuildResized"/> would refuse to clone this stock package because of its names
    /// (read only; the format is <see cref="TextureEdits.SourceRefusal"/>'s check): the name map must hold the package
    /// path exactly as <paramref name="stockPath"/> spells it, plus the export name, and nothing else holding the name.
    /// A file key whose folder differs in letter case from the path the package stores (e.g. Mode/lobby vs Mode/Lobby)
    /// fails here. Null when it can be cloned.
    /// </summary>
    public static string? NameRefusal(GameSource source, string stockPath)
    {
        byte[] stock;
        try { stock = source.ReadPackageBytes(stockPath).Bytes; }
        catch (Exception e) { return $"{stockPath} can't be read ({e.Message})"; }
        return NameRenames(stock, stockPath, stockPath[(stockPath.LastIndexOf('/') + 1)..], out _);
    }

    /// <summary>
    /// The name-map renames that give a texture package its new short name: the package path and the export name, as
    /// the name map stores them (without an FName number suffix, which must be the same on both sides). Every name
    /// holding the old base must be one of those two. Returns why not, or null.
    /// </summary>
    private static string? NameRenames(byte[] stock, string stockPath, string newShortName, out List<(string From, string To)> renames)
    {
        renames = [];
        var folder = stockPath[..(stockPath.LastIndexOf('/') + 1)];
        var (oldBase, oldSuffix) = SplitNumber(stockPath[folder.Length..]);
        var (newBase, newSuffix) = SplitNumber(newShortName);
        if (oldSuffix != newSuffix) return $"\"{newShortName}\" must end like \"{oldBase}{oldSuffix}\" (FName number suffix \"{oldSuffix}\")";
        var holders = ZenNameMap.Read(stock).Where(n => n.Text.Contains(oldBase, StringComparison.Ordinal)).Select(n => n.Text).ToList();
        if (!holders.Order(StringComparer.Ordinal).SequenceEqual(new[] { folder + oldBase, oldBase }.Order(StringComparer.Ordinal)))
        {
            // the commonest cause: the package stores its path in another letter case than its file key (Mode/Lobby vs Mode/lobby)
            if (holders.FirstOrDefault(h => h != folder + oldBase && h.Equals(folder + oldBase, StringComparison.OrdinalIgnoreCase)) is { } stored)
                return $"{GamePaths.ShortName(stockPath)} stores its path as {stored}, its file as {folder + oldBase}: the letter case differs, so the clone's renames don't match";
            return $"names holding \"{oldBase}\": {string.Join(", ", holders)} (expected exactly the package path and the export name)";
        }
        renames = [(folder + oldBase, folder + newBase), (oldBase, newBase)];
        return null;
    }

    /// <summary>One colour for every pixel of every mip (B, G, R, A as stored).</summary>
    public static Func<int, int, byte[]> Solid(byte r, byte g, byte b, byte a = 255) =>
        (w, h) => Enumerable.Range(0, w * h).SelectMany(_ => new[] { b, g, r, a }).ToArray();
}
