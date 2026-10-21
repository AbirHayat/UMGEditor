using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>
/// Re-points a soft object path (e.g. a uiImage's texture) in one stock package to another object of the same path
/// length: the path is a name in the Zen name map, so this is a same-length name rename (NameRenamePatch: exact byte
/// diff, and the re-parse may differ only by the rename). Branch experiment, canary C13.
/// </summary>
public static class SoftPathRepoint
{
    public sealed record Result(ModPackage? Package, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    /// <param name="from">The stock object path, e.g. "/Game/…/Tex720/DemoTitlePlate.DemoTitlePlate"</param>
    public static Result Build(GameSource source, string packagePath, string from, string to)
    {
        var (fileKey, stock) = source.ReadPackageBytes(packagePath);
        var r = NameRenamePatch.Apply(source, fileKey, stock, [(from, to)]);
        return r.Ok ? new Result(new ModPackage(packagePath, fileKey, source.ChunkIdHex(fileKey), r.Chunk!), [], r.Log) : new Result(null, r.Refusals, r.Log);
    }
}
