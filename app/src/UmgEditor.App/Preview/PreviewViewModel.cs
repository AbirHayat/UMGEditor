using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmgEditor.App.ViewModels;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;

namespace UmgEditor.App.Preview;

public sealed record ResolutionPreset(string Name, int Width, int Height)
{
    public override string ToString() => Width > 0 ? $"{Name}  ({Width}×{Height})" : Name;
}

/// <summary>Feature 3: lays the selected widget package out at a chosen resolution and draws it.</summary>
public sealed class PreviewViewModel : ObservableObject
{
    public static readonly ResolutionPreset Custom = new("Custom", 0, 0);

    public IReadOnlyList<ResolutionPreset> Presets { get; } =
    [
        // validated against stock.png: the rendered viewport has the window area's aspect, not exactly 16:9
        new("Your game view (LDPlayer, measured)", 1786, 1000),
        new("LDPlayer setting 1600×900 (240 dpi)", 1600, 900),
        new("Design 1920×1080", 1920, 1080),
        new("1280×720", 1280, 720),
        new("Phone 19.5:9", 2340, 1080),
        new("Phone 20:9", 2400, 1080),
        new("Phone 20:9 QHD", 2712, 1220),
        new("iPhone 6.1\"", 2532, 1170),
        new("Tablet 4:3", 2048, 1536),
        Custom,
    ];

    private readonly string _workDir;
    private LayoutEngine? _engine;
    private WidgetPackageTree? _tree;
    private int _arrangeVersion;

    public PreviewViewModel(string workDir)
    {
        _workDir = workDir;
        _preset = Presets[0];
        _width = _preset.Width;
        _height = _preset.Height;
        ExportCommand = new RelayCommand(Export, () => Layout?.Root != null);
        LoadUnderlayCommand = new RelayCommand(LoadUnderlay);
        ClearUnderlayCommand = new RelayCommand(() => Underlay = null, () => Underlay != null);
    }

    public PreviewRenderer? Renderer { get; private set; }

    public void Attach(LayoutEngine engine, PreviewRenderer renderer)
    {
        _engine = engine;
        Renderer = renderer;
        OnPropertyChanged(nameof(Renderer));
        Rearrange();
    }

    public void Show(WidgetPackageTree? tree)
    {
        _tree = tree;
        Rearrange();
    }

    private EditSet? _edits;
    /// <summary>The current project's edits, laid out on top of the stock values (an edited child shows in its users too).</summary>
    public EditSet? Edits
    {
        get => _edits;
        set { _edits = value; Rearrange(); }
    }

    private ResolutionPreset _preset;
    public ResolutionPreset SelectedPreset
    {
        get => _preset;
        set
        {
            if (!Set(ref _preset, value) || value == Custom) return;
            _width = value.Width; _height = value.Height;
            OnPropertyChanged(nameof(ViewportWidth)); OnPropertyChanged(nameof(ViewportHeight));
            Rearrange();
        }
    }

    private int _width, _height;
    public int ViewportWidth { get => _width; set { if (value > 0 && Set(ref _width, value)) { MarkCustom(); Rearrange(); } } }
    public int ViewportHeight { get => _height; set { if (value > 0 && Set(ref _height, value)) { MarkCustom(); Rearrange(); } } }

    private void MarkCustom()
    {
        if (_preset.Width == _width && _preset.Height == _height) return;
        _preset = Presets.FirstOrDefault(p => p.Width == _width && p.Height == _height) ?? Custom;
        OnPropertyChanged(nameof(SelectedPreset));
    }

    private LayoutResult? _layout;
    public LayoutResult? Layout { get => _layout; private set { if (Set(ref _layout, value)) OnPropertyChanged(nameof(ScaleInfo)); } }

    /// <summary>DPI rule, ScaleBox decisions and which axis limits them.</summary>
    public string ScaleInfo
    {
        get
        {
            if (Layout is not { } l) return "";
            var dpi = $"DPI {l.DpiScale:0.####} ({l.Settings.Rule} → {l.DpiRuleInput:0} px, {(l.Settings.Source == Core.Layout.ValueSource.Config ? "game config" : "assumed")})";
            var boxes = l.ScaleBoxes.Take(3).Select(b => $"{b.Widget} {b.Stretch} ×{b.Applied:0.####}: {b.LimitingAxis} limits (x {b.ScaleX:0.####}, y {b.ScaleY:0.####})");
            var unit = l.Root?.Descendants().FirstOrDefault(b => b.Node.ClassName == "CanvasPanel" && b.Parent?.Node.ClassName == "ScaleBox")?.PixelsPerUnit ?? l.DpiScale;
            return string.Join("   ·   ", new[] { dpi }.Concat(boxes).Append($"1 design unit = {unit:0.#####} px"));
        }
    }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    // ---- overlay toggles ----
    private bool _outlines, _clipRects, _assumed = true, _hidden, _animated;
    public bool ShowOutlines { get => _outlines; set { if (Set(ref _outlines, value)) OnPropertyChanged(nameof(Options)); } }
    public bool ShowClipRects { get => _clipRects; set { if (Set(ref _clipRects, value)) OnPropertyChanged(nameof(Options)); } }
    public bool HighlightAssumed { get => _assumed; set { if (Set(ref _assumed, value)) OnPropertyChanged(nameof(Options)); } }
    public bool ShowHidden { get => _hidden; set { if (Set(ref _hidden, value)) OnPropertyChanged(nameof(Options)); } }
    public bool HighlightAnimated { get => _animated; set { if (Set(ref _animated, value)) OnPropertyChanged(nameof(Options)); } }

    private WidgetNode? _selected;
    public WidgetNode? Selected { get => _selected; set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(Options)); OnPropertyChanged(nameof(SelectedBox)); } } }

    public LayoutBox? SelectedBox => Selected == null ? null : Layout?.All().FirstOrDefault(b => ReferenceEquals(b.Node, Selected));

    public PreviewOptions Options => new(_outlines, _clipRects, _assumed, _hidden, _animated, _selected);

    // ---- screenshot underlay ----
    private BitmapSource? _underlay;
    public BitmapSource? Underlay { get => _underlay; private set => Set(ref _underlay, value); }
    private double _underlayOpacity = 0.5;
    public double UnderlayOpacity { get => _underlayOpacity; set => Set(ref _underlayOpacity, value); }
    private Int32Rect _crop = Int32Rect.Empty;
    /// <summary>"x,y,w,h" of the game view inside the screenshot.</summary>
    public string UnderlayCropText
    {
        get => _crop.IsEmpty ? "" : $"{_crop.X},{_crop.Y},{_crop.Width},{_crop.Height}";
        set
        {
            var p = value.Split(',').Select(s => int.TryParse(s.Trim(), out var n) ? n : -1).ToArray();
            if (p.Length == 4 && p.All(n => n >= 0)) { _crop = new Int32Rect(p[0], p[1], p[2], p[3]); OnPropertyChanged(nameof(UnderlayCrop)); }
            OnPropertyChanged();
        }
    }
    public Int32Rect UnderlayCrop => _crop;

    public RelayCommand ExportCommand { get; }
    public RelayCommand LoadUnderlayCommand { get; }
    public RelayCommand ClearUnderlayCommand { get; }

    private void LoadUnderlay()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Screenshots|*.png;*.jpg;*.jpeg", InitialDirectory = Path.GetFullPath(Path.Combine(_workDir, "..", "ss")) };
        if (dlg.ShowDialog() != true) return;
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(dlg.FileName); bmp.EndInit(); bmp.Freeze();
        Underlay = bmp;
        // LDPlayer at 1920x1080: the game view measured at x 50..1835, y 32..1031
        if (bmp.PixelWidth == 1920 && bmp.PixelHeight == 1080 && _crop.IsEmpty) UnderlayCropText = "50,32,1786,1000";
        Status = $"Underlay {Path.GetFileName(dlg.FileName)} ({bmp.PixelWidth}×{bmp.PixelHeight}), crop {UnderlayCropText}";
    }

    private void Rearrange()
    {
        if (_engine == null || _tree == null) { Layout = null; return; }
        var version = ++_arrangeVersion;
        var (engine, tree, w, h, edits) = (_engine, _tree, _width, _height, _edits);
        Status = "Laying out…";
        Task.Run(() => engine.Arrange(tree, w, h, edits)).ContinueWith(t =>
        {
            if (version != _arrangeVersion) return;
            if (t.Exception != null) { Status = "Layout failed: " + t.Exception.InnerException?.Message; Layout = null; return; }
            Layout = t.Result;
            var approx = t.Result.All().Count(b => b.Approximation != null);
            var invisible = t.Result.All().Count(b => !b.Visible || b.Opacity == 0);
            var edited = t.Result.All().Count(b => b.Inputs.Any(i => i.Source == Core.Layout.ValueSource.Edited));
            Status = $"{t.Result.All().Count()} widgets laid out at {w}×{h}" + (edited > 0 ? $" · {edited} use edited values from the project" : "")
                     + (approx > 0 ? $" · {approx} approximated (orange)" : "")
                     + (invisible > 0 ? $" · {invisible} hidden or at opacity 0 in the stored pose (tick Hidden widgets to show them)" : "");
            OnPropertyChanged(nameof(SelectedBox));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>"CanvasPanel_0/ScaleBox_0/.../Mask": unique even when widget names repeat across embedded blueprints.</summary>
    private static string PathOf(LayoutBox b)
    {
        var parts = new List<string>();
        for (var x = b; x != null; x = x.Parent) parts.Add(x.Node.Name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Writes the preview at native viewport resolution plus a JSON of every widget rect (px and design units).</summary>
    private void Export()
    {
        if (Layout is not { Root: not null } layout || Renderer == null || _tree == null) return;
        var dir = Path.Combine(_workDir, "out", "preview");
        Directory.CreateDirectory(dir);
        var name = $"{GamePaths.ShortName(_tree.PackagePath)}_{layout.ViewportWidth}x{layout.ViewportHeight}";

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Renderer.Render(dc, layout, Options with { Selected = null }, 1.0, new Vector(0, 0));
        var rtb = new RenderTargetBitmap(layout.ViewportWidth, layout.ViewportHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(Path.Combine(dir, name + ".png"))) encoder.Save(f);

        var rects = layout.All().Select(b => new
        {
            b.Node.Name, Path = PathOf(b), Class = b.Node.ClassName, Package = GamePaths.ShortName(b.Node.PackagePath), b.Visible,
            Px = new { b.Bounds.X, b.Bounds.Y, W = b.Bounds.Width, H = b.Bounds.Height },
            PixelsPerUnit = b.PixelsPerUnit, b.Approximation,
        });
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(new
        {
            Package = _tree.PackagePath, layout.ViewportWidth, layout.ViewportHeight, layout.DpiScale,
            ScaleBoxes = layout.ScaleBoxes, Widgets = rects,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Status = $"Exported {name}.png and .json to {dir}";
    }
}
