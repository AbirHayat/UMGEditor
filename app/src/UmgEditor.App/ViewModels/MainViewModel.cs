using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UmgEditor.App.Preview;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Textures;

namespace UmgEditor.App.ViewModels;

/// <summary>Feature 1: browse every UI package in the stock containers.</summary>
public sealed class MainViewModel : ObservableObject
{
    public const string AllGroups = "(all groups)";
    public const string AllKinds = "(all kinds)";

    private GameConfig _config;
    private readonly string? _configPath;
    private readonly DispatcherTimer _searchDelay;
    private GameSource? _source;
    private IReadOnlyList<UiAssetEntry> _all = [];

    public MainViewModel(GameConfig config, string? configPath = null)
    {
        _config = config;
        _configPath = configPath;
        Preview = new PreviewViewModel(config.WorkDir);
        Preview.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(PreviewViewModel.IsImpact) or nameof(PreviewViewModel.ShowStock)) UpdateHandles(); };
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
        RebuildCommand = new RelayCommand(() => _ = LoadAsync(rebuild: true), () => !IsBusy);
        UndoCommand = new RelayCommand(Undo, () => _controller?.CanUndo == true);
        RedoCommand = new RelayCommand(Redo, () => _controller?.CanRedo == true);
        SaveCommand = new RelayCommand(Save, () => _controller?.AnyDirty == true);
        CreateProjectCommand = new RelayCommand(CreateProject, () => _controller != null && NewProjectName.Trim().Length > 0);
        CopyImpactCommand = new RelayCommand(() => { if (Impact is { } r) CopyText(r.ToText(), $"the impact of the edits ({r.Packages.Count} packages)"); },
            () => Impact is { IsEmpty: false });
        BackToImpactCommand = new RelayCommand(() => MiddleTabIndex = ImpactTabIndex);
        ShowOpenPackageCommand = new RelayCommand(() => Preview.Show(_openTree), () => Preview.IsImpact);
        CopyBytesCommand = new RelayCommand(() => { if (Bytes is { } b) CopyText(b.ToText(), $"the bytes to patch ({b.ValueCount} value{(b.ValueCount == 1 ? "" : "s")})"); },
            () => Bytes is { IsEmpty: false });
        ImportTextureCommand = new RelayCommand(ImportTexture, () => CanReplaceTexture);
        WhiteTextureCommand = new RelayCommand(() => PrepareTrial(TextureEditMode.White, null), () => CanReplaceTexture && TextureInfo?.CloneRefusal == null);
        InjectTextureCommand = new RelayCommand(InjectTrial, () => _trial != null && !TrialRefused && !_trialChecking);
        DiscardTrialCommand = new RelayCommand(() => DiscardTrial("Trial discarded."), () => _trial != null);
        RemoveTextureEditCommand = new RelayCommand(RemoveTextureEdit, () => TextureInfo?.Edit != null);
        OpenTextureUserCommand = new RelayCommand<TextureUserRow>(OpenTextureUser, r => r is { IsThis: false });
    }

    public PreviewViewModel Preview { get; }

    public string PaksDir => _config.PaksDir;
    public RelayCommand RebuildCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _status = "";
    /// <summary>Loading, indexing and the package filter.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _actionStatus = "";
    /// <summary>Status line for project actions (save, undo/redo, discard, new project, copy); the filter doesn't overwrite it.</summary>
    public string ActionStatus { get => _actionStatus; private set => Set(ref _actionStatus, value); }

    private string? _actionDetail;
    /// <summary>Tooltip of the action status line (the full save report after a save).</summary>
    public string? ActionDetail { get => _actionDetail; private set => Set(ref _actionDetail, value); }

    private void SetAction(string text, string? detail = null)
    {
        ActionStatus = $"{DateTime.Now:HH:mm:ss}  {text}";
        ActionDetail = detail;
    }

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
        set
        {
            if (value == _selected) return;
            // the search/filter hid the open package (the list's selection turns null): it stays open, this is no switch
            if (value == null && _selected != null) return;
            // leaving a package with unsaved edits: save, discard or stay (the list goes back to the open package)
            if (_openTree is { } open && _controller?.IsDirty(open.PackagePath) == true
                && !ResolveUnsaved(open.PackagePath, $"opening {value?.Name ?? "another package"}"))
            {
                SwitchRefused?.Invoke(this, EventArgs.Empty);
                return;
            }
            _selected = value;
            OnPropertyChanged();
            _ = OpenTreeAsync(value);
        }
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
            UpdateHandles();
            OnPropertyChanged(nameof(NodeSource));
            OnPropertyChanged(nameof(TrialForSelected));
            ClearEditMessage();
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
            if (_controller.AnyDirty && !ResolveUnsaved(null, $"switching to project {value}"))
            {
                SwitchRefused?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (_controller.SwitchProject(value) is { } why)
            {
                SetAction(why);
                OnPropertyChanged(); // the combo box goes back to the current project
                return;
            }
            _project = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title)); // the controller's Changed ran before _project was set
        }
    }

    public string ProjectInfo => _controller == null ? "" :
        (_controller.Edits.Count == 0 && _controller.TextureEdits.Count == 0 ? $"no edits ({_controller.ProjectFolder})"
        : string.Join(" + ", new[]
        {
            _controller.Edits.Count == 0 ? null : $"{_controller.Edits.Count} edit{(_controller.Edits.Count == 1 ? "" : "s")} in {string.Join(", ", _controller.Sessions.Where(s => s.Current.Count > 0).Select(s => GamePaths.ShortName(s.PackagePath)))}",
            _controller.TextureEdits.Count == 0 ? null : $"{_controller.TextureEdits.Count} texture{(_controller.TextureEdits.Count == 1 ? "" : "s")}",
        }.OfType<string>()))
        + (_controller.AnyDirty ? $" · unsaved: {_controller.UnsavedPackagesText}" : "");

    public string Title => "eFootball Mobile UMG Editor — " + _project + (_controller?.AnyDirty == true ? " *" : "");

    private string _newProjectName = "";
    public string NewProjectName
    {
        get => _newProjectName;
        // New follows the name even when it isn't typed (pasted, or set by UI Automation): no input event re-queries it
        set { if (Set(ref _newProjectName, value ?? "")) CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>A package or project switch was cancelled: the list and picker must show the open one again.</summary>
    public event EventHandler? SwitchRefused;

    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CreateProjectCommand { get; }

    // ---- 6c: editing ----

    private string? _editMessage;
    /// <summary>Result of the last edit (applied, refused and why, not applied).</summary>
    public string? EditMessage { get => _editMessage; private set => Set(ref _editMessage, value); }

    private bool _editRefused;
    public bool EditRefused { get => _editRefused; private set => Set(ref _editRefused, value); }

    private IReadOnlyList<WarningView> _editWarnings = [];
    /// <summary>Warnings for the edit just applied (rescale, other widgets moving, leaving a clip area).</summary>
    public IReadOnlyList<WarningView> LastEditWarnings { get => _editWarnings; private set => Set(ref _editWarnings, value); }

    private void ClearEditMessage()
    {
        EditMessage = null;
        EditRefused = false;
        LastEditWarnings = [];
    }

    /// <summary>A value typed into the inspector (Enter). Returns true when it was applied.</summary>
    public bool CommitField(FieldInspection field, string text)
    {
        if (_controller is not { } c || _openTree is not { } tree || SelectedNode?.Node is not { } node) return false;
        if (text.Trim() == field.DisplayValue) return false;
        return RunEdit(node, field.ShortPath, confirmed => c.SetValue(tree, node, field.Path, text, confirmed),
            () => $"{node.Name}.{field.ShortPath} = {SlotEditor.Format(SlotEditor.F32(EditorController.ParseNumber(text).Value))}");
    }

    /// <summary>
    /// Runs an edit through the first-edit gate (the confirmation once per package) and reports it under the inspector:
    /// applied (with its pending-edit warnings), refused and why, or not applied. True when applied. Typed values,
    /// drags and nudges all go through here, so they behave the same.
    /// </summary>
    private bool RunEdit(WidgetNode node, string what, Func<bool, EditOutcome> apply, Func<string> applied)
    {
        var outcome = apply(false);
        if (outcome.NeedsConfirmation)
        {
            if (Ask(outcome.ConfirmationText + "\n\nApply this edit?", $"First edit of {GamePaths.ShortName(node.PackagePath)}",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                EditMessage = $"{what}: not applied.";
                EditRefused = false;
                return false;
            }
            outcome = apply(true);
        }
        if (outcome.Refusal is { } why)
        {
            EditMessage = $"{what}: refused. {why}";
            EditRefused = true;
            LastEditWarnings = [];
            return false;
        }
        if (!outcome.Applied) return false;
        EditMessage = $"{applied()} (unsaved; Ctrl+S saves, Ctrl+Z undoes).";
        EditRefused = false;
        LastEditWarnings = WarningView.Of(outcome.PendingWarnings);
        return true;
    }

    // ---- step 3: textures (see, import, preview as a trial, inject) ----

    private TextureCache? _textures;
    public RelayCommand ImportTextureCommand { get; }
    public RelayCommand WhiteTextureCommand { get; }
    public RelayCommand InjectTextureCommand { get; }
    public RelayCommand DiscardTrialCommand { get; }
    public RelayCommand RemoveTextureEditCommand { get; }
    public RelayCommand<TextureUserRow> OpenTextureUserCommand { get; }

    private TextureInspection? _textureInfo;
    /// <summary>The selected image widget's texture, who else draws it and its edit (null for other widgets).</summary>
    public TextureInspection? TextureInfo
    {
        get => _textureInfo;
        private set { if (Set(ref _textureInfo, value)) { OnPropertyChanged(nameof(CanReplaceTexture)); OnPropertyChanged(nameof(TrialForSelected)); CommandManager.InvalidateRequerySuggested(); } }
    }

    private BitmapSource? _textureThumbnail;
    public BitmapSource? TextureThumbnail { get => _textureThumbnail; private set => Set(ref _textureThumbnail, value); }

    private BitmapSource? _editThumbnail;
    /// <summary>The image the project's edit (or the trial) puts on this widget.</summary>
    public BitmapSource? EditThumbnail { get => _editThumbnail; private set => Set(ref _editThumbnail, value); }

    public bool CanReplaceTexture => _controller != null && TextureInfo is { Blocked: null };

    // the trial: an imported image shown in the preview but not in the project yet
    private (TextureEdit Edit, DecodedImage Image, string? File, TextureEditMode Mode)? _trial;
    private int _trialVersion;
    private bool _trialChecking;
    public bool HasTrial => _trial != null;
    /// <summary>The trial belongs to the selected widget (its controls show in the inspector).</summary>
    public bool TrialForSelected => _trial is { } t && SelectedNode?.Node is { } n && t.Edit.Package.Equals(n.PackagePath, StringComparison.OrdinalIgnoreCase) && t.Edit.Widget == n.Name;

    private string _trialName = "";
    /// <summary>The new texture's short name (same length as the source's; pre-filled with a free one).</summary>
    public string TrialName { get => _trialName; set { if (Set(ref _trialName, value ?? "")) RePrepareTrial(); } }

    private bool _trialMarginsZero;
    public bool TrialMarginsZero { get => _trialMarginsZero; set { if (Set(ref _trialMarginsZero, value)) RePrepareTrial(); } }

    private bool _trialAllUsers;
    public bool TrialAllUsers { get => _trialAllUsers; set { if (Set(ref _trialAllUsers, value)) RePrepareTrial(); } }

    private string? _trialSource;
    /// <summary>The B8G8R8A8 texture the clone comes from when the widget's own texture can't be cloned.</summary>
    public string? TrialSource { get => _trialSource; set { if (Set(ref _trialSource, value)) RePrepareTrial(); } }

    private string? _trialCheck;
    /// <summary>What the trial would do (new packages, re-points, warnings) or why it's refused.</summary>
    public string? TrialCheck { get => _trialCheck; private set => Set(ref _trialCheck, value); }

    private bool _trialRefused;
    public bool TrialRefused { get => _trialRefused; private set => Set(ref _trialRefused, value); }

    private void ImportTexture()
    {
        if (TextureInfo == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = $"Image for {SelectedNode?.Name} (replaces {TextureInfo.Name})" };
        if (dlg.ShowDialog() != true) return;
        PrepareTrial(TextureEditMode.Image, dlg.FileName);
    }

    /// <summary>Makes a trial for the selected widget: imports the PNG, shows it in the preview, checks it in the background.</summary>
    private void PrepareTrial(TextureEditMode mode, string? file, bool keepName = false)
    {
        if (_controller is not { } c || SelectedNode?.Node is not { } node || TextureInfo is not { } info) return;
        var source = info.CloneRefusal == null ? null : _trialSource ?? info.Sources.FirstOrDefault();
        if (info.CloneRefusal != null && source == null) { SetTrialRefused($"{info.CloneRefusal}, and no B8G8R8A8 texture has a fitting path."); return; }
        var name = keepName && _trialName.Trim().Length > 0 ? _trialName.Trim() : null;
        var (edit, image, why) = c.PrepareTextureEdit(node.PackagePath, node.Name, mode, file, name, source, _trialMarginsZero,
            _trialAllUsers ? TextureEditScope.AllUsers : TextureEditScope.Widget);
        if (edit == null) { SetTrialRefused(why!); return; }
        image ??= c.ImageOf(edit);
        if (image == null) { SetTrialRefused("the white copy can't be made: the texture isn't B8G8R8A8"); return; }
        _trial = (edit, image, file, mode);
        _trialName = edit.NewName; OnPropertyChanged(nameof(TrialName));
        if (source != _trialSource) { _trialSource = source; OnPropertyChanged(nameof(TrialSource)); }
        EditThumbnail = PreviewRenderer.ToBitmap(image);
        Preview.TrialLabel = $"{(file != null ? Path.GetFileName(file) : "white copy")} {image.Width}×{image.Height} on {GamePaths.ShortName(edit.Package)} › {edit.Widget}";
        RefreshOverrides();
        OnTrialChanged();
        MiddleTabIndex = PreviewTabIndex;
        _ = CheckTrialAsync();
    }

    private void RePrepareTrial()
    {
        if (_trial is { } t && TrialForSelected) PrepareTrial(t.Mode, t.File, keepName: true);
    }

    private void SetTrialRefused(string why)
    {
        TrialCheck = why;
        TrialRefused = true;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Plans the project's texture edits with the trial in the background: the tools' own checks, nothing written.</summary>
    private async Task CheckTrialAsync()
    {
        if (_controller is not { } c || _trial is not { } t) return;
        var version = ++_trialVersion;
        var edits = c.TextureEdits.Where(e => !(e.Package.Equals(t.Edit.Package, StringComparison.OrdinalIgnoreCase) && e.Widget == t.Edit.Widget)).Append(t.Edit).ToList();
        var saved = c.SavedChunks(edits); // on the UI thread: sessions aren't thread-safe
        _trialChecking = true;
        TrialCheck = "Checking (clones, re-point, union with the saved edits)…";
        TrialRefused = false;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var plan = await Task.Run(() => c.PlanTextures(edits, saved));
            if (version != _trialVersion) return;
            if (!plan.Ok) { SetTrialRefused(string.Join("\n", plan.Refusals)); return; }
            var item = plan.Items[^1];
            TrialCheck = $"OK: {item.StockWidth}×{item.StockHeight} → {item.Width}×{item.Height}. New package{(item.ClonePaths.Count == 1 ? "" : "s")}: {string.Join(", ", item.ClonePaths.Select(GamePaths.ShortName).Distinct())} " +
                         $"({string.Join(", ", item.ClonePaths.Select(p => p.Contains("/0164F/") ? "0164F" : "Tex720"))}). Re-pointed: {string.Join(", ", item.Storing.Select(GamePaths.ShortName))}." +
                         (item.Warnings.Count > 0 ? "\n" + string.Join("\n", item.Warnings) : "");
            TrialRefused = false;
        }
        catch (Exception e)
        {
            if (version == _trialVersion) SetTrialRefused("Check failed: " + e.Message);
        }
        finally
        {
            if (version == _trialVersion) { _trialChecking = false; CommandManager.InvalidateRequerySuggested(); }
        }
    }

    private void InjectTrial()
    {
        if (_controller is not { } c || _trial is not { } t || SelectedNode?.Node is not { } node) return;
        var what = $"{t.Edit.Widget} texture → {t.Edit.NewName}";
        if (RunEdit(node, what, confirmed => c.ApplyTextureEdit(t.Edit, confirmed), () => $"{what} ({t.Image.Width}×{t.Image.Height})"))
        {
            _trial = null;
            Preview.TrialLabel = null;
            OnTrialChanged();
            RefreshOverrides();
            SetAction($"Injected the image into project {c.Project}: {what} (unsaved; Ctrl+S saves).");
        }
    }

    private void DiscardTrial(string? message)
    {
        if (_trial == null) return;
        _trial = null;
        ++_trialVersion;
        _trialChecking = false;
        TrialCheck = null;
        TrialRefused = false;
        Preview.TrialLabel = null;
        OnTrialChanged();
        RefreshOverrides();
        _ = InspectAsync();
        if (message != null) SetAction(message);
    }

    private void OnTrialChanged()
    {
        OnPropertyChanged(nameof(HasTrial));
        OnPropertyChanged(nameof(TrialForSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveTextureEdit()
    {
        if (_controller is not { } c || SelectedNode?.Node is not { } node) return;
        if (c.RemoveTextureEdit(node.PackagePath, node.Name))
        {
            EditMessage = $"{node.Name}: texture edit removed (unsaved; Ctrl+Z brings it back).";
            EditRefused = false;
            SetAction($"Removed the texture edit of {node.Name}.");
        }
    }

    /// <summary>The preview draws the project's texture edits and the trial.</summary>
    private void RefreshOverrides()
    {
        if (_controller is not { } c) return;
        Preview.TextureOverrides = c.OverridesOf(null, _trial is { } t ? (t.Edit, t.Image) : null);
    }

    /// <summary>"Used by" row click: opens that package and selects the widget.</summary>
    private void OpenTextureUser(TextureUserRow? row)
    {
        if (row == null) return;
        var entry = _all.FirstOrDefault(e => GamePaths.ToPackagePath(e.Path).Equals(row.Package, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { SetAction($"{row.PackageName} isn't in the package list."); return; }
        if (_openTree?.PackagePath.Equals(row.Package, StringComparison.OrdinalIgnoreCase) == true) { SelectByName(row.Widget); return; }
        _selectAfterOpen = row.Widget;
        Selected = entry;
    }

    private string? _selectAfterOpen;

    private void SelectByName(string widget)
    {
        foreach (var root in TreeRoots)
            if (FindByName(root, widget) is { } vm)
            {
                ExpandParents(TreeRoots, vm);
                vm.IsSelected = true;
                // a row whose container isn't realized yet doesn't raise the tree's selection event: select it here too
                SelectedNode = vm;
                MiddleTabIndex = TreeTabIndex;
                return;
            }

        static WidgetNodeViewModel? FindByName(WidgetNodeViewModel v, string name) =>
            v.Node is { IsReadOnly: false } n && n.Name == name ? v : v.Children.Select(c => FindByName(c, name)).FirstOrDefault(x => x != null);

        static bool ExpandParents(IEnumerable<WidgetNodeViewModel> list, WidgetNodeViewModel target)
        {
            foreach (var v in list)
                if (v == target || ExpandParents(v.Children, target)) { if (v != target) v.IsExpanded = true; return true; }
            return false;
        }
    }

    // ---- 6f: dragging and nudging in the preview ----

    private SlotDrag? _drag;

    /// <summary>The preview's handles for the selected widget (or why there are none).</summary>
    private void UpdateHandles()
    {
        Preview.Handles = _controller is not { } c || SelectedNode?.Node is not { } node || _openTree == null ? null
            : Preview.IsImpact ? DragAvailability.Blocked($"the preview shows {GamePaths.ShortName(Preview.ImpactPackagePath ?? "")} from the impact panel; press \"Open package\" to drag in {GamePaths.ShortName(_openTree.PackagePath)}.")
            : Preview.ShowStock ? DragAvailability.Blocked("the preview shows stock (no edits); untick \"Stock (no edits)\" to drag.")
            : c.DragHandles(node);
    }

    /// <summary>Mouse-down + move on a handle: starts a drag at the preview's viewport. False (with the reason on the status line) when it can't.</summary>
    public bool BeginDrag(DragHandle handle)
    {
        if (_controller is not { } c || _openTree is not { } tree || SelectedNode?.Node is not { } node || Preview.IsImpact || Preview.ShowStock) return false;
        var (drag, why) = c.BeginDrag(tree, node, handle, Preview.ViewportWidth, Preview.ViewportHeight);
        if (drag == null) { SetAction($"Can't drag {node.Name}: {why}"); return false; }
        _drag = drag;
        return true;
    }

    public void DragTo(float dxPx, float dyPx, bool free)
    {
        if (_drag is not { } drag) return;
        var step = drag.Update(dxPx, dyPx, free);
        Preview.ShowDragStep(step.Layout, step.Text + (free ? "  · free (Ctrl)" : ""), step.Warnings);
    }

    /// <summary>Mouse-up commits the drag as one edit (asking first for a package's first edit); Esc cancels it.</summary>
    public void EndDrag(bool commit)
    {
        if (_drag is not { } drag || _openTree is not { } tree || _controller is not { } c) return;
        _drag = null;
        Preview.EndDrag();
        if (!commit) { SetAction($"Drag cancelled: {drag.Node.Name} is unchanged."); return; }
        if (drag.Last is not { } step || step.IsNoOp) return;
        if (RunEdit(drag.Node, drag.Node.Name, confirmed => c.CommitDrag(tree, drag, confirmed), () => $"{drag.Node.Name}: {step.Text} (drag)"))
            SetAction($"Dragged {drag.Node.Name}: {step.Text}.");
    }

    /// <summary>Arrow key in the preview: one unit (one undo step).</summary>
    public void Nudge(int dx, int dy)
    {
        if (_controller is not { } c || _openTree is not { } tree || SelectedNode?.Node is not { } node) return;
        if (Preview.Handles is { Any: false } blocked) { EditMessage = $"{node.Name}: can't nudge. {blocked.Reason}"; EditRefused = true; return; }
        var before = c.Edits;
        if (RunEdit(node, node.Name, confirmed => c.Nudge(tree, node, dx, dy, confirmed), () => $"{node.Name} nudged {Describe(before, c.Edits)}"))
            SetAction($"Nudged {node.Name} by ({dx}, {dy}) unit.");
    }

    private static string Describe(EditSet before, EditSet after) =>
        string.Join(", ", after.Values.Where(kv => !before.TryGet(kv.Key, out var v) || !v.Equals(kv.Value))
            .Select(kv => $"{kv.Key.Path.Replace("LayoutData.", "")} = {SlotEditor.Format(kv.Value)}"));

    private void Undo()
    {
        if (_controller is not { } c || c.NextUndo is not { } package || !c.Undo()) return;
        ClearEditMessage();
        SetAction(package == EditorController.TexturesStep ? "Undid a texture edit." : $"Undid an edit in {GamePaths.ShortName(package)}.");
    }

    private void Redo()
    {
        if (_controller is not { } c || c.NextRedo is not { } package || !c.Redo()) return;
        ClearEditMessage();
        SetAction(package == EditorController.TexturesStep ? "Redid a texture edit." : $"Redid an edit in {GamePaths.ShortName(package)}.");
    }

    private void Save() => SaveAndReport(null);

    /// <summary>
    /// Saves (one package, or all when null). A save shows on the status line, with the full report in its tooltip;
    /// only refusals open the dialog. Returns true when nothing was refused.
    /// </summary>
    private bool SaveAndReport(string? package)
    {
        if (_controller is not { } c) return false;
        var textures = package == null && c.TexturesDirty ? c.TextureEdits.Count : -1;
        var results = package == null ? c.SaveAll() : c.SavePackage(package);
        var report = EditorController.SaveReport(results);
        var ok = results.All(r => r.Result.Saved);
        // texture edits are one file for the project (textures.json), saved with everything else
        var texturesText = textures < 0 ? "" : $"Saved {textures} texture edit{(textures == 1 ? "" : "s")} ({Path.Combine(c.ProjectFolder, TextureEditStore.FileName)}).";
        SetAction(results.Count == 0 && textures >= 0 ? texturesText : EditorController.SaveStatus(results) + (texturesText.Length > 0 ? " " + texturesText : ""), report);
        if (!ok) Ask(report, "Save refused", MessageBoxButton.OK, MessageBoxImage.Error);
        return ok;
    }

    /// <summary>
    /// Unsaved edits in a package (or the whole project when null) before an action that leaves them: Yes saves,
    /// No discards them (an undoable step), Cancel stays. Returns true when the action may go ahead.
    /// </summary>
    private bool ResolveUnsaved(string? package, string action)
    {
        if (_controller is not { } c) return true;
        var what = package == null ? c.UnsavedPackagesText : GamePaths.ShortName(package);
        var answer = Ask($"Unsaved edits in {what}. Save them before {action}?\n\n" +
                         "Yes = save. No = discard them (Ctrl+Z brings them back while the editor is open). Cancel = stay here.",
            "Unsaved edits", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) return SaveAndReport(package);
        if (answer != MessageBoxResult.No) return false;
        c.DiscardUnsaved(package);
        SetAction($"Discarded the unsaved edits in {what} (Ctrl+Z brings them back).");
        return true;
    }

    /// <summary>Window closing: false keeps the window open.</summary>
    public bool ConfirmClose()
    {
        if (_controller is not { AnyDirty: true } c) return true;
        var answer = Ask($"Unsaved edits in {c.UnsavedPackagesText}. Save before closing?\n\n" +
                         "Yes = save. No = close without saving (the last save stays as it is). Cancel = stay.",
            "Unsaved edits", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveAndReport(null);
    }

    private void CreateProject()
    {
        if (_controller is not { } c) return;
        var name = NewProjectName.Trim();
        if (c.AnyDirty && !ResolveUnsaved(null, $"creating project {name}")) return;
        if (c.CreateProject(name) is { } why) { SetAction("New project: " + why); return; }
        Projects = EditorController.Projects(_config);
        _project = c.Project;
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(Title));
        NewProjectName = "";
        SetAction($"Created project {c.Project} ({c.ProjectFolder}).");
    }

    // ---- 6g: export ----

    /// <summary>The export dialog's model for the current project (null until the containers are open).</summary>
    public ExportViewModel? CreateExport() => _controller is { } c ? new ExportViewModel(c, _config.RetocPath, SaveRetocPath) : null;

    /// <summary>The retoc path is a setting: remembered in umgeditor.json (next to the exe) when an export starts.</summary>
    private void SaveRetocPath(string path)
    {
        if (path == _config.RetocPath) return;
        _config = _config with { RetocPath = path };
        if (_configPath == null) return;
        try
        {
            _config.Save(_configPath);
            SetAction($"retoc path saved to {_configPath}: {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetAction($"Couldn't save the retoc path to {_configPath}: {e.Message}");
        }
    }

    // ---- 6e: impact ----

    public const int TreeTabIndex = 0, PreviewTabIndex = 1, BytesTabIndex = 2, ImpactTabIndex = 3;

    private int _middleTab;
    /// <summary>The middle column's tab (Tree, Preview, Bytes to patch, Impact).</summary>
    public int MiddleTabIndex { get => _middleTab; set => Set(ref _middleTab, value); }

    private ImpactReport? _impact;
    private int _impactVersion;
    /// <summary>Every package the project's edits reach, stock vs edited; rebuilt in the background after every change.</summary>
    public ImpactReport? Impact
    {
        get => _impact;
        private set { if (Set(ref _impact, value)) OnPropertyChanged(nameof(ImpactTabHeader)); }
    }

    public string ImpactTabHeader => Impact is { IsEmpty: false } r ? $"Impact ({r.Packages.Count})" : "Impact";

    private string? _impactStatus;
    /// <summary>"Laying out…" while the report is rebuilt, or why it failed.</summary>
    public string? ImpactStatus { get => _impactStatus; private set => Set(ref _impactStatus, value); }

    public RelayCommand CopyImpactCommand { get; }
    public RelayCommand BackToImpactCommand { get; }
    public RelayCommand ShowOpenPackageCommand { get; }

    private async Task RefreshImpactAsync()
    {
        var version = ++_impactVersion;
        if (_controller is not { } c) return;
        var edits = c.Edits; // snapshot on the UI thread; laying out runs in the background
        ImpactStatus = "Laying out every package the edits reach…";
        try
        {
            var report = await Task.Run(() => c.ImpactOf(edits));
            if (version != _impactVersion) return;
            Impact = report;
            ImpactStatus = null;
            // a package shown from the panel follows the edits
            if (Preview.ImpactPackagePath is { } shown)
            {
                var p = report.Packages.FirstOrDefault(x => x.PackagePath.Equals(shown, StringComparison.OrdinalIgnoreCase));
                Preview.UpdateImpact(ImpactLabel(p, shown), ChangedPaths(p));
            }
        }
        catch (Exception e)
        {
            if (version != _impactVersion) return;
            ImpactStatus = "Couldn't lay out the affected packages: " + e.Message;
        }
    }

    /// <summary>Impact panel click: the package in the Preview tab with the project's edits, its changed boxes in green.</summary>
    public async Task PreviewImpactAsync(ImpactPackage package)
    {
        try
        {
            var tree = await LoadTree(package.PackagePath, readOnly: false);
            Preview.ShowImpact(tree, ImpactLabel(package, package.PackagePath), ChangedPaths(package));
            MiddleTabIndex = PreviewTabIndex;
            SetAction($"Previewing {package.Name} with the project's edits ({package.ChangeText}).");
        }
        catch (Exception e)
        {
            SetAction($"Couldn't preview {package.Name}: {e.Message}");
        }
    }

    private static IReadOnlySet<string> ChangedPaths(ImpactPackage? p) => (p?.Boxes.Select(b => b.Path) ?? []).ToHashSet();

    private static string ImpactLabel(ImpactPackage? p, string packagePath) => p == null
        ? $"{GamePaths.ShortName(packagePath)}: no longer reached by the project's edits."
        : $"{p.Name}{(p.IsOnline ? " (online)" : "")} · {p.Note} · {p.ChangeText}";

    // ---- 6d: bytes to patch ----

    private BytesReport? _bytes;
    private int _bytesVersion;
    /// <summary>What the bytes-to-patch tab shows; rebuilt after every change to the project.</summary>
    public BytesReport? Bytes
    {
        get => _bytes;
        private set { if (Set(ref _bytes, value)) { OnPropertyChanged(nameof(BytesTabHeader)); OnPropertyChanged(nameof(BytesSummary)); } }
    }

    public string BytesTabHeader => Bytes is { IsEmpty: false } b ? $"Bytes to patch ({b.ValueCount}{(TextureBytes.Count > 0 ? $" + {TextureBytes.Count} texture{(TextureBytes.Count == 1 ? "" : "s")}" : "")})"
        : TextureBytes.Count > 0 ? $"Bytes to patch ({TextureBytes.Count} texture{(TextureBytes.Count == 1 ? "" : "s")})" : "Bytes to patch";

    /// <summary>The Bytes tab's headline: the values to patch, or the texture edits when there are only those.</summary>
    public string BytesSummary => Bytes is { IsEmpty: true } && TextureBytes.Count > 0
        ? $"{TextureBytes.Count} texture edit{(TextureBytes.Count == 1 ? "" : "s")} in project {_controller?.Project}: new texture packages and re-pointed paths below (no values to patch)."
        : Bytes?.Summary ?? "Loading…";

    private IReadOnlyList<TextureBytesRow> _textureBytes = [];
    /// <summary>Step 3: one row per texture edit (new packages, re-points).</summary>
    public IReadOnlyList<TextureBytesRow> TextureBytes
    {
        get => _textureBytes;
        private set { if (Set(ref _textureBytes, value)) { OnPropertyChanged(nameof(BytesTabHeader)); OnPropertyChanged(nameof(BytesSummary)); } }
    }

    private string? _textureBytesStatus;
    public string? TextureBytesStatus { get => _textureBytesStatus; private set => Set(ref _textureBytesStatus, value); }

    private string? _bytesError;
    public string? BytesError { get => _bytesError; private set => Set(ref _bytesError, value); }

    public RelayCommand CopyBytesCommand { get; }

    /// <summary>Puts text on the clipboard and says so on the status line.</summary>
    public void CopyText(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            SetAction($"Copied {what} to the clipboard.");
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            SetAction($"Couldn't copy {what}: the clipboard is in use ({e.Message}).");
        }
    }

    private async Task RefreshBytesAsync()
    {
        var version = ++_bytesVersion;
        if (_controller is not { } c) return;
        // widget names come from the package trees: load them off the UI thread first (cached), then plan here
        try { await Task.WhenAll(c.Sessions.Select(s => LoadTree(s.PackagePath, readOnly: false)).ToList()); }
        catch (Exception) { /* a tree that doesn't load only costs the widget name */ }
        if (version != _bytesVersion) return;
        try
        {
            Bytes = c.BytesToPatch();
            BytesError = null;
        }
        catch (Exception e)
        {
            Bytes = null;
            BytesError = "Couldn't plan the bytes to patch: " + e.Message;
        }
        // step 3: the texture edits (clones + re-points), planned in the background on the saved edit sets
        var edits = c.TextureEdits;
        if (edits.Count == 0) { TextureBytes = []; TextureBytesStatus = null; return; }
        var saved = c.SavedChunks(edits);
        TextureBytesStatus = "Planning the texture edits…";
        try
        {
            var plan = await Task.Run(() => c.PlanTextures(edits, saved));
            if (version != _bytesVersion) return;
            TextureBytes = TextureBytesRow.Of(plan);
            TextureBytesStatus = plan.Ok ? null : "Refused: " + string.Join("\n", plan.Refusals);
        }
        catch (Exception e)
        {
            if (version == _bytesVersion) TextureBytesStatus = "Couldn't plan the texture edits: " + e.Message;
        }
    }

    private static MessageBoxResult Ask(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
        Application.Current?.MainWindow is { IsLoaded: true } w ? MessageBox.Show(w, text, caption, buttons, icon) : MessageBox.Show(text, caption, buttons, icon);

    private NodeInspection? _inspection;
    /// <summary>The inspector's data for the selected widget (fields, greyed reasons, boards, embedding packages, warnings).</summary>
    public NodeInspection? Inspection { get => _inspection; private set => Set(ref _inspection, value); }

    private string _inspectionStatus = "";
    public string InspectionStatus { get => _inspectionStatus; private set => Set(ref _inspectionStatus, value); }

    private async Task InspectAsync()
    {
        var version = ++_inspectVersion;
        var (controller, tree, node, textures) = (_controller, _openTree, SelectedNode?.Node, _textures);
        if (controller == null || tree == null || node == null) { Inspection = null; TextureInfo = null; InspectionStatus = ""; return; }
        InspectionStatus = "Checking…";
        try
        {
            var result = await Task.Run(() => controller.Inspect(tree, node));
            if (version != _inspectVersion) return;
            Inspection = result;
            InspectionStatus = "";
            // step 3: the texture section (usage index, clone sources) and its thumbnails
            var texture = await Task.Run(() => controller.InspectTexture(node));
            if (version != _inspectVersion) return;
            TextureInfo = texture;
            var stock = texture != null && textures != null ? await Task.Run(() => textures.Get(texture.Texture)) : null;
            if (version != _inspectVersion) return;
            TextureThumbnail = stock is { IsPlaceholder: false } ? PreviewRenderer.ToBitmap(stock) : null;
            var edited = TrialForSelected ? _trial!.Value.Image : texture?.Edit is { } e ? controller.ImageOf(e) : null;
            EditThumbnail = edited != null ? PreviewRenderer.ToBitmap(edited) : null;
            if (texture?.CloneRefusal != null && _trialSource == null) { _trialSource = texture.Sources.FirstOrDefault(); OnPropertyChanged(nameof(TrialSource)); }
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
        RefreshOverrides();
        Preview.Edits = _controller.Edits;
        UpdateHandles();
        OnPropertyChanged(nameof(ProjectInfo));
        OnPropertyChanged(nameof(Title));
        CommandManager.InvalidateRequerySuggested();
        _ = InspectAsync();
        _ = RefreshBytesAsync();
        _ = RefreshImpactAsync();
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
        DiscardTrial(null);
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
            if (_selectAfterOpen is { } widget) { _selectAfterOpen = null; SelectByName(widget); }
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
            var textures = _textures = new TextureCache(source, new AssetRipperTextureDecoder());
            var scale = await Task.Run(() => UiScaleSettings.Load(source));
            WidgetPackageTree? Embedded(string p) { try { return LoadTree(p, readOnly: true).GetAwaiter().GetResult(); } catch { return null; } }
            (int, int)? Stock(string t) => textures.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null;
            var engine = new LayoutEngine(scale, Embedded, Stock);
            // the preview's engine also sees replacement images (an auto-sized image takes their size); the controller's stays stock
            Preview.Attach(new LayoutEngine(scale, Embedded, t => Preview.SizeOverride(t) ?? Stock(t)), new PreviewRenderer(textures.Get));
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
        OnPropertyChanged(nameof(Selected)); // the open package's row is highlighted again when the filter shows it
        var total = Visible.Count();
        Status =$"Showing {Items.Count:N0} of {total:N0} UI packages" + (_includeBulkImages ? "" : $" ({_all.Count - total:N0} thumbnails/emblems hidden)");
    }
}
