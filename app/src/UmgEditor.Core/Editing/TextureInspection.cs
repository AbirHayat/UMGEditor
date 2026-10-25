using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Editing;

/// <summary>
/// One widget that names the inspected texture, for the inspector's "Used by" list. A reference type: WPF keys item
/// automation peers by Equals, and equal rows would vanish from UI Automation.
/// </summary>
public sealed class TextureUserRow
{
    public required string Package { get; init; }
    public required string Widget { get; init; }
    /// <summary>"uiImage_C.texture"</summary>
    public required string What { get; init; }
    public bool IsThis { get; init; }
    /// <summary>Same package and same path as the inspected widget: scope This package changes it too, This widget doesn't.</summary>
    public bool SamePackage { get; init; }
    public bool IsOnline { get; init; }
    /// <summary>"shown in 8: MatchTime, MatchTimeCard, …" or "".</summary>
    public string ShownIn { get; init; } = "";
    public string PackageName => GamePaths.ShortName(Package);
    public string Label => $"{PackageName} › {Widget}";
    /// <summary>"MatchTimeConnection_Img_Logo": for the row's AutomationId</summary>
    public string Id => $"{PackageName}_{Widget}";
    public string Note => IsThis ? "this widget" : SamePackage ? "same package" : "";
}

/// <summary>The inspector's Texture section for a uiImage: what it draws, who else draws it, and its edit in the project.</summary>
public sealed class TextureInspection
{
    public required TextureRef Ref { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Format { get; init; }
    public required int Mips { get; init; }
    public required IReadOnlyList<string> ThemeCopies { get; init; }
    /// <summary>Why this texture can't be cloned (not B8G8R8A8), or null</summary>
    public string? CloneRefusal { get; init; }
    /// <summary>B8G8R8A8 textures a clone can come from when this one can't (best first, see <see cref="Modding.TextureEdits.Sources"/>)</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
    public PreviewRaster.Margins Margins { get; init; }
    public required IReadOnlyList<TextureUserRow> Users { get; init; }
    /// <summary>Why texture edits aren't possible here (a library widget), or null</summary>
    public string? Blocked { get; init; }
    public TextureEdit? Edit { get; init; }

    public string Texture => Ref.Texture;
    public string Name => GamePaths.ShortName(Ref.Texture);
    public string SizeText => $"{Width}×{Height} · {Format.Replace("PF_", "")} · {Mips} mip{(Mips == 1 ? "" : "s")}";
    public string ThemeText => "Theme copies: " + string.Join(", ", ThemeCopies.Select(p => p[..p.LastIndexOf('/')][(p.IndexOf("/Widget/", StringComparison.Ordinal) + 8)..]));
    public string? MarginsText => Margins.Any ? $"9-slice margins {Margins.Left:0.##} / {Margins.Top:0.##} / {Margins.Right:0.##} / {Margins.Bottom:0.##}: borders keep their size, the middle stretches (Margins 0 stretches the whole image)" : null;
    public string UsersHeader => Users.Count == 1 ? "Used by this widget only" : $"Used by {Users.Count} widgets";
    public bool IsShared => Users.Count > 1;
    public string? EditText => Edit is not { } e ? null
        : $"Replaced in this project: {(e.Mode == TextureEditMode.White ? "white copy" : "your image")} → {e.NewName}{(e.MarginsZero ? ", margins 0" : "")}{e.Scope switch { TextureEditScope.AllUsers => ", every package naming it", TextureEditScope.Package => ", every widget of this package naming it", _ => ", this widget only" }}{(e.Source != null ? $", cloned from {GamePaths.ShortName(e.Source)}" : "")}";
}

/// <summary>One texture edit in the Bytes tab: what it changes and the packages it ships (a reference type, for UIA).</summary>
public sealed class TextureBytesRow
{
    public required string Widget { get; init; }
    public required string Change { get; init; }
    public required string Path { get; init; }
    public required string Packages { get; init; }
    public required string Notes { get; init; }

    public static IReadOnlyList<TextureBytesRow> Of(TexturePlan plan) => plan.Items.Select(i =>
    {
        var e = i.Edit;
        var clones = plan.Clones.Where(c => i.ClonePaths.Contains(c.PackagePath, StringComparer.OrdinalIgnoreCase)).ToList();
        var repointed = plan.Packages.Where(p => i.Storing.Contains(p.PackagePath, StringComparer.OrdinalIgnoreCase)).ToList();
        return new TextureBytesRow
        {
            Widget = $"{GamePaths.ShortName(e.Package)} › {e.Widget}",
            Change = $"{GamePaths.ShortName(e.Texture)} {i.StockWidth}×{i.StockHeight} → {e.NewName} {i.Width}×{i.Height}{(e.Mode == TextureEditMode.White ? " (white)" : "")}{(e.MarginsZero ? " · margins 0" : "")}",
            Path = i.Pointed.Count == i.Storing.Count
                ? $"name map: {i.To} appended ({i.To.Length} characters); {(i.Following.Count == 0 ? e.Widget : $"{e.Widget} + {i.Following.Count} more")} pointed at it, {i.From} kept"
                : $"name map: {i.From}\n       → {i.To}  (renamed, same length, {i.To.Length} characters)" + (i.Pointed.Count > 0 ? $"; appended + pointed in {string.Join(", ", i.Pointed.Select(GamePaths.ShortName))}" : ""),
            Packages = string.Join("\n", clones.Select(c => $"new {c.PackagePath[(c.PackagePath.IndexOf("/Widget/", StringComparison.Ordinal) + 8)..]}  {c.Chunk.Length:N0} bytes (clone of {GamePaths.ShortName(c.CloneOf ?? "")})")
                .Concat(repointed.Select(p => $"re-pointed {GamePaths.ShortName(p.PackagePath)}  {p.Chunk.Length:N0} bytes"))),
            Notes = string.Join("\n", i.Warnings),
        };
    }).ToList();
}
