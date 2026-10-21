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
        var holders = ZenNameMap.Read(stock).Where(n => n.Text.Contains(oldShortName, StringComparison.Ordinal)).Select(n => n.Text).ToList();
        if (holders.Order(StringComparer.Ordinal).SequenceEqual(new[] { stockPath, oldShortName }.Order(StringComparer.Ordinal)) == false)
            return Refuse($"names holding \"{oldShortName}\": {string.Join(", ", holders)} (expected exactly the package path and the export name)");
        var renamed = NameRenamePatch.Apply(source, fileKey, stock, [(stockPath, newPath), (oldShortName, newShortName)], writes);
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

    /// <summary>One colour for every pixel of every mip (B, G, R, A as stored).</summary>
    public static Func<int, int, byte[]> Solid(byte r, byte g, byte b, byte a = 255) =>
        (w, h) => Enumerable.Range(0, w * h).SelectMany(_ => new[] { b, g, r, a }).ToArray();
}
