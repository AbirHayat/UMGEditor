using System.IO;
using System.Windows.Threading;
using UmgEditor.App.Preview;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Textures;

namespace UmgEditor.App.ViewModels;

/// <summary>Feature 1: browse every UI package in the stock containers.</summary>
public sealed class MainViewModel : ObservableObject
{
    public const string AllGroups = "(all groups)";
    public const string AllKinds = "(all kinds)";

    private readonly GameConfig _config;
    private readonly DispatcherTimer _searchDelay;
    private GameSource? _source;
    private IReadOnlyList<UiAssetEntry> _all = [];

    public MainViewModel(GameConfig config)
    {
        _config = config;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
        RebuildCommand = new RelayCommand(() => _ = LoadAsync(rebuild: true), () => !IsBusy);
        Preview = new PreviewViewModel(config.WorkDir);
    }

    public PreviewViewModel Preview { get; }

    public string PaksDir => _config.PaksDir;
    public RelayCommand RebuildCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private IReadOnlyList<UiAssetEntry> _items = [];
    public IReadOnlyList<UiAssetEntry> Items { get => _items; private set => Set(ref _items, value); }

    private IReadOnlyList<string> _groups = [AllGroups];
    public IReadOnlyList<string> Groups { get => _groups; private set => Set(ref _groups, value); }

    public IReadOnlyList<string> Kinds { get; } = [AllKinds, .. Enum.GetNames<UiAssetKind>()];

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) { _searchDelay.Stop(); _searchDelay.Start(); } }
    }

    private string _selectedGroup = AllGroups;
    public string SelectedGroup { get => _selectedGroup; set { if (Set(ref _selectedGroup, value ?? AllGroups)) ApplyFilter(); } }

    private string _selectedKind = nameof(UiAssetKind.Widget);
    public string SelectedKind { get => _selectedKind; set { if (Set(ref _selectedKind, value ?? AllKinds)) ApplyFilter(); } }

    private bool _includeBulkImages;
    /// <summary>Player thumbnails and emblems (~74k images); hidden by default.</summary>
    public bool IncludeBulkImages { get => _includeBulkImages; set { if (Set(ref _includeBulkImages, value)) { RefreshGroups(); ApplyFilter(); } } }

    private UiAssetEntry? _selected;
    public UiAssetEntry? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) _ = OpenTreeAsync(value); }
    }

    // ---- feature 2: widget tree of the selected package ----

    private UsageGraph? _usage;
    private readonly Dictionary<string, Task<WidgetPackageTree>> _trees = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<WidgetNodeViewModel> _treeRoots = [];
    public IReadOnlyList<WidgetNodeViewModel> TreeRoots { get => _treeRoots; private set => Set(ref _treeRoots, value); }

    private string _treeStatus = "Select a widget package to see its hierarchy.";
    public string TreeStatus { get => _treeStatus; private set => Set(ref _treeStatus, value); }

    private string _packageBoards = "";
    /// <summary>Scoreboards that show the opened package (directly or through other widgets).</summary>
    public string PackageBoards { get => _packageBoards; private set => Set(ref _packageBoards, value); }

    private WidgetNodeViewModel? _selectedNode;
    public WidgetNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            Preview.Selected = value?.Node;
            OnPropertyChanged(nameof(NodeSource));
            _ = InspectAsync();
        }
    }

    // ---- step 6: project (edit set) and inspector ----

    private EditorController? _controller;
    private WidgetPackageTree? _openTree;
    private int _inspectVersion;

    private IReadOnlyList<string> _projects = [EditorController.DefaultProject];
    /// <summary>"default" (work\edits) plus every named edit set (work\edits\NAME).</summary>
    public IReadOnlyList<string> Projects { get => _projects; private set => Set(ref _projects, value); }

    private string _project = EditorController.DefaultProject;
    public string SelectedProject
    {
        get => _project;
        set
        {
            if (value == null || value == _project || _controller == null) return;
            if (_controller.SwitchProject(value) is { } why)
            {
                Status = why;
                OnPropertyChanged(); // the combo box goes back to the current project
                return;
            }
            _project = value;
            OnPropertyChanged();
        }
    }

    public string ProjectInfo => _controller == null ? "" :
        _controller.Edits.Count == 0 ? $"no edits ({_controller.ProjectFolder})"
        : $"{_controller.Edits.Count} edit{(_controller.Edits.Count == 1 ? "" : "s")} in {string.Join(", ", _controller.Sessions.Where(s => s.Current.Count > 0).Select(s => GamePaths.ShortName(s.PackagePath)))}";

    private NodeInspection? _inspection;
    /// <summary>The inspector's data for the selected widget (fields, greyed reasons, boards, embedding packages, warnings).</summary>
    public NodeInspection? Inspection { get => _inspection; private set => Set(ref _inspection, value); }

    private string _inspectionStatus = "";
    public string InspectionStatus { get => _inspectionStatus; private set => Set(ref _inspectionStatus, value); }

    private async Task InspectAsync()
    {
        var version = ++_inspectVersion;
        var (controller, tree, node) = (_controller, _openTree, SelectedNode?.Node);
        if (controller == null || tree == null || node == null) { Inspection = null; InspectionStatus = ""; return; }
        InspectionStatus = "Checking…";
        try
        {
            var result = await Task.Run(() => controller.Inspect(tree, node));
            if (version != _inspectVersion) return;
            Inspection = result;
            InspectionStatus = "";
        }
        catch (Exception e)
        {
            if (version != _inspectVersion) return;
            Inspection = null;
            InspectionStatus = "Inspection failed: " + e.Message;
        }
    }

    private void OnProjectChanged()
    {
        if (_controller == null) return;
        Preview.Edits = _controller.Edits;
        OnPropertyChanged(nameof(ProjectInfo));
        _ = InspectAsync();
    }

    public string NodeSource => SelectedNode?.Node is not { } n ? "" :
        n.IsReadOnly ? $"Embedded from {GamePaths.ShortName(n.PackagePath)} — read-only here; open that package to edit it." : $"In {GamePaths.ShortName(n.PackagePath)} (export #{n.ExportIndex})";

    /// <summary>Preview click: select the matching tree row if it exists, otherwise show the widget on its own.</summary>
    public WidgetNodeViewModel SelectFromPreview(LayoutBox box)
    {
        var vm = Find(TreeRoots, box.Node);
        if (vm != null) { ExpandTo(TreeRoots, vm); vm.IsSelected = true; }
        else SelectedNode = vm = new WidgetNodeViewModel(box.Node, p => LoadTree(p, readOnly: true));
        return vm;

        static WidgetNodeViewModel? Find(IEnumerable<WidgetNodeViewModel> list, WidgetNode node)
        {
            foreach (var v in list)
            {
                if (ReferenceEquals(v.Node, node)) return v;
                if (Find(v.Children, node) is { } hit) return hit;
            }
            return null;
        }

        static bool ExpandTo(IEnumerable<WidgetNodeViewModel> list, WidgetNodeViewModel target)
        {
            foreach (var v in list)
                if (v == target || ExpandTo(v.Children, target)) { if (v != target) v.IsExpanded = true; return true; }
            return false;
        }
    }

    private string BoardsText(string packagePath)
    {
        if (_usage == null) return "";
        var boards = _usage.ScoreboardsUsing(packagePath);
        var boardText = boards.Count == 0 ? "no scoreboard" : string.Join(", ", boards.Select(b => b.Label));
        var embeddedBy = EditImpact.AffectedPackages(_usage, [packagePath]).Where(a => !a.Edited).ToList();
        return $"{boardText}  ·  {EditWarnings.EmbeddedByText(GamePaths.ShortName(packagePath), embeddedBy)}";
    }

    private Task<WidgetPackageTree> LoadTree(string packagePath, bool readOnly)
    {
        lock (_trees)
        {
            var key = (readOnly ? "ro:" : "rw:") + packagePath;
            if (!_trees.TryGetValue(key, out var task))
                _trees[key] = task = Task.Run(() => WidgetTreeReader.Build(_source!, packagePath, readOnly));
            return task;
        }
    }

    private async Task OpenTreeAsync(UiAssetEntry? entry)
    {
        SelectedNode = null;
        TreeRoots = [];
        PackageBoards = "";
        _openTree = null;
        Preview.Show(null);
        if (entry == null || _source == null) { TreeStatus = "Select a widget package to see its hierarchy."; return; }
        var packagePath = GamePaths.ToPackagePath(entry.Path);
        PackageBoards = BoardsText(packagePath);
        if (entry.Kind != UiAssetKind.Widget) { TreeStatus = $"{entry.Name} is a {entry.Kind}, not a widget blueprint."; return; }

        TreeStatus = $"Loading {entry.Name}…";
        try
        {
            var tree = await LoadTree(packagePath, readOnly: false);
            if (Selected != entry) return; // the user moved on
            Func<string, Task<WidgetPackageTree>> loadEmbedded = p => LoadTree(p, readOnly: true);
            var roots = new List<WidgetNodeViewModel>();
            if (tree.Root != null) roots.Add(new WidgetNodeViewModel(tree.Root, loadEmbedded) { IsExpanded = true });
            roots.AddRange(tree.Unattached.Select(n => new WidgetNodeViewModel(n, loadEmbedded)));
            TreeRoots = roots;
            _openTree = tree;
            Preview.Show(tree);
            var animated = tree.All().Count(n => n.IsAnimated);
            TreeStatus = tree.Root == null
                ? $"{entry.Name} has no layout of its own (empty WidgetTree): a base/template class filled in by subclasses or code."
                : $"{tree.WidgetCount} widgets · {animated} animated · {tree.AnimationNames.Count} animations";
        }
        catch (Exception e)
        {
            TreeStatus = "Failed to read widget tree: " + e.Message;
        }
    }

    public async Task LoadAsync(bool rebuild = false)
    {
        IsBusy = true;
        try
        {
            Status = "Opening containers…";
            _source ??= await Task.Run(() => GameSource.Open(_config));
            var progress = new Progress<(int Done, int Total)>(p => Status = $"Indexing UI packages… {p.Done:N0} / {p.Total:N0}");
            var source = _source;
            _all = await Task.Run(() => rebuild ? RebuildCache(source, progress) : UiAssetIndex.LoadOrBuild(source, progress));
            _usage = new UsageGraph(_all);
            var textures = new TextureCache(source, new AssetRipperTextureDecoder());
            var scale = await Task.Run(() => UiScaleSettings.Load(source));
            var engine = new LayoutEngine(scale,
                p => { try { return LoadTree(p, readOnly: true).GetAwaiter().GetResult(); } catch { return null; } },
                t => textures.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null);
            Preview.Attach(engine, new PreviewRenderer(textures.Get));
            var usage = _usage;
            _controller = await Task.Run(() => new EditorController(source, usage, engine, p => LoadTree(p, readOnly: false).GetAwaiter().GetResult()));
            _controller.Changed += (_, _) => OnProjectChanged();
            Projects = EditorController.Projects(_config);
            OnProjectChanged();
            RefreshGroups();
            ApplyFilter();
        }
        catch (Exception e)
        {
            Status = "Failed to load: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IReadOnlyList<UiAssetEntry> RebuildCache(GameSource source, IProgress<(int, int)> progress)
    {
        foreach (var f in Directory.EnumerateFiles(source.Config.CacheDir, "ui_index_*.json")) File.Delete(f);
        return UiAssetIndex.LoadOrBuild(source, progress);
    }

    private IEnumerable<UiAssetEntry> Visible => _includeBulkImages ? _all : _all.Where(e => !e.IsBulkImageSet);

    private void RefreshGroups()
    {
        var groups = Visible.GroupBy(e => e.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).ToList();
        Groups = [AllGroups, .. groups];
        if (!Groups.Contains(SelectedGroup)) SelectedGroup = AllGroups;
    }

    private void ApplyFilter()
    {
        var terms = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = Enum.TryParse<UiAssetKind>(_selectedKind, out var k) ? k : (UiAssetKind?) null;
        Items = Visible
            .Where(e => _selectedGroup == AllGroups || e.Group == _selectedGroup)
            .Where(e => kind == null || e.Kind == kind)
            .Where(e => terms.All(t => e.Path.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var total = Visible.Count();
        Status = $"Showing {Items.Count:N0} of {total:N0} UI packages" + (_includeBulkImages ? "" : $" ({_all.Count - total:N0} thumbnails/emblems hidden)");
    }
}
