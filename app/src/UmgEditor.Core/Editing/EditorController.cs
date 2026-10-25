using System.Text.Json;
using System.Text.RegularExpressions;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Editing;

/// <summary>Why a field (or a whole slot) is greyed out, grouped so the UI can explain each kind once.</summary>
public enum GreyReason { None, Library, EmbeddedReadOnly, NoSlot, NotCanvasSlot, NotStored, Boolean, NotLayoutValue }

/// <summary>Text typed into an inspector field compared with the value that is applied.</summary>
public enum TypedState { Applied, Pending, Invalid }

/// <summary>One slot field as the inspector shows it: stock value, whether it's stored or edited, and why it may be greyed.</summary>
public sealed record FieldInspection(string Path, string StockValue, bool Stored, double? EditedValue, GreyReason Grey, string? Reason)
{
    public bool Editable => Grey == GreyReason.None;
    public bool IsEdited => EditedValue != null;
    /// <summary>Not stored but editable: saving adds it as a new 29-byte tag, so the package grows (branch experiment, canary C10).</summary>
    public bool IsInsertion => Editable && !Stored;
    public string State => IsEdited ? Stored ? "edited" : "new tag" : Stored ? "stored" : "default";
    public string DisplayValue => EditedValue is { } v ? SlotEditor.Format(v) : StockValue;
    public string ShortPath => Path.Replace("LayoutData.", "");
    /// <summary>Why it's greyed, or the stock value when edited, or what editing a value that isn't stored does.</summary>
    public string? ToolTip => Reason ?? (IsInsertion
        ? $"Not stored in the file: the engine default {StockValue} applies. {(IsEdited ? "Saving adds" : "Setting it adds")} a 29-byte tag, so the package grows " +
          "(a size-changing edit; device-tested by canary C10). Type the default again (or undo) to leave it out."
        : IsEdited ? $"Stock value {StockValue}. Type it again (or undo) to go back." : null);

    /// <summary>
    /// Whether typed text differs from the applied value: Applied when Enter would change nothing (same text, or a
    /// number that stores as the same value, e.g. "100.0" for 100), Pending for another number, Invalid otherwise.
    /// </summary>
    public TypedState Typed(string text)
    {
        if (!Editable || text.Trim() == DisplayValue) return TypedState.Applied;
        var (value, why) = EditorController.ParseNumber(text);
        if (why != null) return TypedState.Invalid;
        var applied = EditedValue ?? EditSet.Parse(StockValue);
        return SlotEditor.StoredValue(Path, value).Equals(applied) ? TypedState.Applied : TypedState.Pending;
    }

    /// <summary>The cue's tooltip for typed text that isn't applied, null when it is.</summary>
    public string? TypedHint(string text) => Typed(text) switch
    {
        TypedState.Pending => $"Typed {text.Trim()}, not applied: the value is still {DisplayValue}. Enter applies it, Esc reverts.",
        TypedState.Invalid => $"{EditorController.ParseNumber(text).Refusal} The value is still {DisplayValue}; Esc reverts.",
        _ => null,
    };

    // Identity, not value equality: WPF's item automation peers are keyed by Equals, so an unchanged field of a fresh
    // inspection (an equal record) was matched to the previous inspection's discarded row and vanished from UI Automation.
    public bool Equals(FieldInspection? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>Everything the inspector shows for a selected widget (read-only data; 6b).</summary>
public sealed record NodeInspection(WidgetNode Node, string? SlotReason, GreyReason SlotGrey, IReadOnlyList<FieldInspection> Fields, PreEditReport Report)
{
    private const int MaxListed = 30;

    /// <summary>Every animation track bound to this widget (or its slot), with what it does and its keys' bytes (step 4a).</summary>
    public IReadOnlyList<Animation.WidgetAnimationTrack> Animations { get; init; } = [];
    public string AnimationsHeader => Animations.Count == 0 ? "Not animated."
        : $"{Animations.Count} track{(Animations.Count == 1 ? "" : "s")} in {Animations.Select(a => a.Animation).Distinct().Count()} animation{(Animations.Select(a => a.Animation).Distinct().Count() == 1 ? "" : "s")}; " +
          $"{Animations.Count(a => a.Does.Count > 0)} change something.";

    public IReadOnlyList<AffectedPackage> EmbeddedByOffline => Report.EmbeddedBy.Where(a => !a.IsOnline).ToList();
    public IReadOnlyList<AffectedPackage> EmbeddedByOnline => Report.EmbeddedBy.Where(a => a.IsOnline).ToList();

    // display helpers for the inspector
    public string BoardsText => Report.Boards.Count == 0 ? "no scoreboard" : string.Join(", ", Report.Boards.Select(b => b.Label));
    public string EmbeddedByHeader => Report.EmbeddedBy.Count == 0
        ? $"No other package embeds {GamePaths.ShortName(Node.PackagePath)}; only its own layout changes."
        : $"Embedded by {Report.EmbeddedBy.Count:N0} package{(Report.EmbeddedBy.Count == 1 ? "" : "s")}, which change with it:";
    /// <summary>The offline list, capped (library widgets have thousands).</summary>
    public IReadOnlyList<AffectedPackage> EmbeddedByOfflineShown => EmbeddedByOffline.Take(MaxListed).ToList();
    public string? EmbeddedByOfflineMore => EmbeddedByOffline.Count > MaxListed ? $"… and {EmbeddedByOffline.Count - MaxListed:N0} more" : null;
    public bool HasOnline => EmbeddedByOnline.Count > 0;
    public string OnlineHeader => $"Online ({EmbeddedByOnline.Count:N0})";
    public IReadOnlyList<AffectedPackage> EmbeddedByOnlineShown => EmbeddedByOnline.Take(MaxListed).ToList();
    public bool HasWarnings => Warnings.Count > 0;
    public string SlotTitle => Node.Slot is { } s ? $"Slot {s.Name} ({s.ClassName})" : "No slot";
    /// <summary>Selection warnings except the boards line (which the inspector shows as lists).</summary>
    public IReadOnlyList<EditWarning> Warnings => Report.Warnings.Where(w => w.Kind != WarningKind.Boards).ToList();

    /// <summary>Greyed fields grouped by reason, so each reason is explained once.</summary>
    public IReadOnlyList<GreyGroup> GreyGroups =>
        SlotGrey != GreyReason.None ? [] :
        Fields.Where(f => !f.Editable).GroupBy(f => f.Grey)
            .Select(g => new GreyGroup(g.Key, EditorController.Explain(g.Key), g.Select(f => f.ShortPath).ToList())).ToList();
}

public sealed record GreyGroup(GreyReason Reason, string Text, IReadOnlyList<string> Fields)
{
    public string FieldsText => string.Join(", ", Fields);
}

/// <summary>Result of an edit request through the controller.</summary>
public sealed record EditOutcome(bool Applied, string? Refusal, string? ConfirmationText, IReadOnlyList<EditWarning> PendingWarnings)
{
    public bool NeedsConfirmation => !Applied && Refusal == null && ConfirmationText != null;
    public static EditOutcome Refused(string why) => new(false, why, null, []);
}

/// <summary>
/// The editor's model, independent of WPF: one project (= edit set: work\edits for "default", work\edits\&lt;name&gt; for a
/// named one) with a PatchSession per edited package, inspection of a selected widget, edits through SlotEditor with the
/// first-edit gate and pending-edit warnings, a project-wide undo/redo, save, impact and mod building.
/// </summary>
public sealed class EditorController
{
    public const string DefaultProject = "default";
    private static readonly Regex ProjectName = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,40}$");
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { DefaultProject, "Assets", "Platforms" };

    private readonly GameSource _source;
    private readonly UsageGraph _usage;
    private readonly LayoutEngine _engine;
    private readonly Func<string, WidgetPackageTree> _loadTree;
    private readonly int _width, _height;
    private readonly WriteGuard _guard;
    private readonly Dictionary<string, PatchSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _undo = new(), _redo = new();
    private FirstEditGate _gate = new();
    private readonly Lazy<TextureUsage> _textureUsage;
    private IReadOnlyList<TextureEdit> _textures = [], _texturesSaved = [];
    private readonly Stack<IReadOnlyList<TextureEdit>> _texturesUndo = new(), _texturesRedo = new();
    /// <summary>The project-wide undo stack's entry for a texture-edit step (the others are package paths).</summary>
    public const string TexturesStep = "textures";

    /// <param name="textureUsage">Which widgets name which texture (default: built from the UI index, cached in work\cache)</param>
    public EditorController(GameSource source, UsageGraph usage, LayoutEngine engine, Func<string, WidgetPackageTree> loadTree,
        int viewportWidth = 1786, int viewportHeight = 1000, string project = DefaultProject, Func<TextureUsage>? textureUsage = null)
    {
        _textureUsage = new Lazy<TextureUsage>(textureUsage ?? (() => TextureUsage.LoadOrBuild(source, UiAssetIndex.LoadOrBuild(source))));
        _source = source;
        _usage = usage;
        _engine = engine;
        _loadTree = loadTree;
        (_width, _height) = (viewportWidth, viewportHeight);
        _guard = new WriteGuard(source.Config);
        if (ProjectNameRefusal(project) is { } bad && project != DefaultProject) throw new ArgumentException(bad);
        Project = project;
        LoadProjectSessions();
    }

    public string Project { get; private set; }
    public event EventHandler? Changed;

    // ---- projects ----

    /// <summary>"default" plus every named edit set under work\edits (folders that aren't package roots or test folders).</summary>
    public static IReadOnlyList<string> Projects(GameConfig config)
    {
        IEnumerable<string> named = Directory.Exists(config.EditsDir)
            ? Directory.GetDirectories(config.EditsDir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('_') && !ReservedNames.Contains(n)).Order(StringComparer.OrdinalIgnoreCase)
            : [];
        return [DefaultProject, .. named];
    }

    public static string? ProjectNameRefusal(string name) =>
        ReservedNames.Contains(name) ? $"\"{name}\" is reserved." :
        !ProjectName.IsMatch(name) ? "A project name is 1–41 letters, digits, _ . - and starts with a letter or digit." : null;

    /// <summary>Switches to another edit set. Refused while there are unsaved edits.</summary>
    public string? SwitchProject(string name)
    {
        if (name.Equals(Project, StringComparison.OrdinalIgnoreCase)) return null;
        if (name != DefaultProject && ProjectNameRefusal(name) is { } bad) return bad;
        if (AnyDirty) return $"Project {Project} has unsaved edits in {UnsavedPackagesText}. Save or undo them first.";
        Project = name;
        _sessions.Clear();
        _undo.Clear();
        _redo.Clear();
        _texturesUndo.Clear();
        _texturesRedo.Clear();
        _gate = new FirstEditGate();
        LoadProjectSessions();
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Creates an empty named edit set (work\edits\&lt;name&gt;) and switches to it. Refused while there are unsaved edits.</summary>
    public string? CreateProject(string name)
    {
        name = name.Trim();
        if (ProjectNameRefusal(name) is { } bad) return bad;
        if (Projects(_source.Config).Contains(name, StringComparer.OrdinalIgnoreCase)) return $"Project {name} already exists; pick it from the list.";
        if (AnyDirty) return $"Project {Project} has unsaved edits in {UnsavedPackagesText}. Save or discard them first.";
        _guard.CreateDirectory(Path.Combine(_guard.Root, name));
        return SwitchProject(name);
    }

    public string ProjectFolder => Project == DefaultProject ? _guard.Root : Path.Combine(_guard.Root, Project);

    /// <summary>Opens a session for every package that has saved edits in this project, so their edits show everywhere.</summary>
    private void LoadProjectSessions()
    {
        _textures = _texturesSaved = Directory.Exists(ProjectFolder) ? TextureEditStore.Load(ProjectFolder) : [];
        if (!Directory.Exists(ProjectFolder)) return;
        // the default project's packages live under edits\Assets\…; named projects are folders next to it and are skipped
        IEnumerable<string> roots = Project == DefaultProject
            ? new[] { "Assets", "Platforms" }.Select(r => Path.Combine(ProjectFolder, r)).Where(Directory.Exists)
            : [ProjectFolder];
        foreach (var manifest in roots.SelectMany(r => Directory.EnumerateFiles(r, "*.edits.json", SearchOption.AllDirectories)))
        {
            string? package;
            try { package = JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("Package").GetString(); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { continue; }
            if (package != null && !_sessions.ContainsKey(package)) Session(package);
        }
    }

    // ---- sessions ----

    public PatchSession Session(string package)
    {
        if (!_sessions.TryGetValue(package, out var s))
            _sessions[package] = s = PatchSession.Open(_source, package, PatchSession.DefaultFolder(_guard, package, Project == DefaultProject ? null : Project));
        return s;
    }

    public IReadOnlyCollection<PatchSession> Sessions => _sessions.Values;
    public IEnumerable<PatchSession> DirtySessions => _sessions.Values.Where(s => s.IsDirty);
    public bool AnyDirty => _sessions.Values.Any(s => s.IsDirty) || TexturesDirty;
    public bool IsDirty(string package) => _sessions.TryGetValue(package, out var s) && s.IsDirty;
    /// <summary>"MatchTimeBase, MatchTime": the packages with unsaved edits, for prompts.</summary>
    public string UnsavedPackagesText => string.Join(", ", DirtySessions.Select(s => GamePaths.ShortName(s.PackagePath)).Concat(TexturesDirty ? ["texture edits"] : []));

    /// <summary>All current edits of the project (every package), for layout: an edited child shows inside its users.</summary>
    public EditSet Edits => _sessions.Values.Aggregate(EditSet.Empty, (all, s) => all.With(s.Current.Values.Select(kv => new FieldChange(kv.Key, double.NaN, kv.Value)), _ => null));

    // ---- inspection ----

    public NodeInspection Inspect(WidgetPackageTree tree, WidgetNode node)
    {
        var warnings = new EditWarnings(_engine, _usage, _width, _height);
        var report = warnings.ForSelection(tree, node);
        var (slotGrey, slotReason) = SlotGreyOf(node, report);
        // only an already-open session: inspecting must not create a working copy on disk
        var edits = _sessions.TryGetValue(node.PackagePath, out var open) ? open.Current : EditSet.Empty;
        var fields = (node.Slot?.Fields ?? []).Select(f =>
        {
            var (grey, reason) = slotGrey != GreyReason.None ? (slotGrey, slotReason) : FieldGreyOf(node, f);
            double? edited = edits.TryGet(FieldKey.Of(node, f.Path), out var v) ? v : null;
            return new FieldInspection(f.Path, f.Value, f.Stored, edited, grey, reason);
        }).ToList();
        return new NodeInspection(node, slotReason, slotGrey, fields, report) { Animations = AnimationsOf(node) };
    }

    private readonly Dictionary<string, IReadOnlyList<Animation.AnimationInfo>> _animations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every animation of a package down to its keys and their bytes (cached; Inspect runs in the background).</summary>
    public IReadOnlyList<Animation.AnimationInfo> Animations(string package)
    {
        lock (_animations)
        {
            if (!_animations.TryGetValue(package, out var list))
                _animations[package] = list = Animation.AnimationReader.Read(_source, package);
            return list;
        }
    }

    /// <summary>The tracks bound to a widget (its own or its slot's), in animation order.</summary>
    public IReadOnlyList<Animation.WidgetAnimationTrack> AnimationsOf(WidgetNode node) =>
        Animations(node.PackagePath).SelectMany(a => a.Tracks.Where(t => t.Widget == node.Name).Select(t => new Animation.WidgetAnimationTrack(a, t))).ToList();

    private (GreyReason, string?) SlotGreyOf(WidgetNode node, PreEditReport report)
    {
        if (GamePaths.IsLibrary(node.PackagePath)) return (GreyReason.Library, SlotEditor.LibraryRefusal(node.PackagePath, report.EmbeddedBy.Count));
        if (SlotEditor.SlotRefusal(node) is not { } why) return (GreyReason.None, null);
        return (node.IsReadOnly ? GreyReason.EmbeddedReadOnly : node.Slot == null ? GreyReason.NoSlot : GreyReason.NotCanvasSlot, why);
    }

    private static (GreyReason, string?) FieldGreyOf(WidgetNode node, SlotField f)
    {
        if (SlotEditor.FieldRefusal(node, f.Path) is not { } why) return (GreyReason.None, null);
        return (f.Path == "bAutoSize" ? GreyReason.Boolean : !f.Stored && why.Contains("not stored") ? GreyReason.NotStored : GreyReason.NotLayoutValue, why);
    }

    public static string Explain(GreyReason reason) => reason switch
    {
        GreyReason.NotStored => "Not stored in the file (the engine default applies), and neither is the rest of its struct. Adding a whole struct or a top-level tag isn't supported yet; only a missing Offsets value can be added.",
        GreyReason.Boolean => "Boolean fields aren't editable in this version.",
        GreyReason.NotLayoutValue => "Not an editable layout value.",
        GreyReason.Library => "Konami library widget: editing is blocked.",
        GreyReason.EmbeddedReadOnly => "Belongs to an embedded package: open that package to edit it.",
        GreyReason.NoSlot => "The root widget has no slot.",
        GreyReason.NotCanvasSlot => "Only CanvasPanelSlot values are editable in this version.",
        _ => "",
    };

    // ---- edits ----

    /// <summary>
    /// Applies a SlotEditor edit to the node's package. The first edit of a package (in this run, when it has no edits yet)
    /// returns NeedsConfirmation with the confirmation text; call again with confirmed: true to apply.
    /// </summary>
    public EditOutcome Apply(WidgetPackageTree tree, WidgetNode node, Func<EditSet, EditResult> edit, bool confirmed = false)
    {
        if (SlotEditor.SlotRefusal(node) is { } blocked)
            return EditOutcome.Refused(GamePaths.IsLibrary(node.PackagePath) ? SlotEditor.LibraryRefusal(node.PackagePath, _usage.AllUsers(node.PackagePath).Count) : blocked);
        var session = Session(node.PackagePath);
        var result = edit(session.Current);
        if (!result.Allowed) return EditOutcome.Refused(result.Reason ?? "not allowed");
        // nothing changes (e.g. the current value typed again): no first-edit prompt, no undo step
        if (result.Changes.All(c => c.OldValue.Equals(c.NewValue))) return new EditOutcome(false, null, null, []);

        var firstEdit = _gate.NeedsConfirmation(node.PackagePath) && session.Current.Count == 0 && session.Saved.Count == 0;
        if (firstEdit && !confirmed)
            return new EditOutcome(false, null, new EditWarnings(_engine, _usage, _width, _height).ForSelection(tree, node).ConfirmationText, []);

        var previous = Edits;
        var after = previous.With(result.Changes, session.BaseValue);
        var pending = new EditWarnings(_engine, _usage, _width, _height).ForPendingEdit(tree, node, after, previous).ToList();
        if (SizeChangeWarning(session, result.Changes) is { } grows) pending.Add(grows);
        var before = session.Current;
        if (session.Apply(result) is { } why) return EditOutcome.Refused(why);
        _gate.Confirm(node.PackagePath);
        if (!ReferenceEquals(before, session.Current))
        {
            _undo.Push(session.PackagePath);
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return new EditOutcome(true, null, null, pending);
    }

    /// <summary>The pending-edit warning when an edit gives a value that isn't stored (saving adds it as a new tag).</summary>
    private static EditWarning? SizeChangeWarning(PatchSession session, IReadOnlyList<FieldChange> changes)
    {
        var added = changes.Where(c => session.StockValue(c.Key) == null && !c.NewValue.Equals(session.BaseValue(c.Key))).ToList();
        if (added.Count == 0) return null;
        var fields = string.Join(", ", added.Select(c => c.Key.Path.Replace("LayoutData.", "")).Distinct());
        return new EditWarning(WarningKind.SizeChange, Severity.Warning,
            $"{fields} {(added.Count == 1 ? "isn't" : "aren't")} stored in {GamePaths.ShortName(session.PackagePath)}: saving adds {(added.Count == 1 ? "a 29-byte tag" : "29-byte tags")}, so the package grows " +
            "and every export after the slot moves. Size-changing edits are device-tested by one canary (C10); test the mod in game.");
    }

    // ---- 6f: dragging in the preview ----

    /// <summary>The preview's handles for a widget, each with why it can't be used (library and embedded widgets: none).</summary>
    public DragAvailability DragHandles(WidgetNode node)
    {
        if (GamePaths.IsLibrary(node.PackagePath))
            return DragAvailability.Blocked(SlotEditor.LibraryRefusal(node.PackagePath, _usage.AllUsers(node.PackagePath).Count));
        return DragAvailability.For(node, Edits);
    }

    /// <summary>Starts a drag at the preview's viewport; null with the reason when that handle can't be used.</summary>
    public (SlotDrag? Drag, string? Refusal) BeginDrag(WidgetPackageTree tree, WidgetNode node, DragHandle handle, int viewportWidth, int viewportHeight)
    {
        var handles = DragHandles(node);
        if (!handles.Allows(handle)) return (null, handles.Refusals[handle]);
        var drag = new SlotDrag(_engine, tree, node, handle, Edits, handles, viewportWidth, viewportHeight);
        return drag.StartBox == null ? (null, $"{node.Name} isn't laid out in this preview.") : (drag, null);
    }

    /// <summary>
    /// Mouse-up: the drag's last step as one ordinary edit (one undo step, the first-edit gate, pending warnings), the
    /// same SlotEditor edit a typed number makes. A drag that ends where it started changes nothing.
    /// </summary>
    public EditOutcome CommitDrag(WidgetPackageTree tree, SlotDrag drag, bool confirmed = false) =>
        drag.Last is not { } step || step.IsNoOp || step.Units == System.Numerics.Vector2.Zero
            ? new EditOutcome(false, null, null, [])
            : Apply(tree, drag.Node, e => drag.EditFor(e, step.Units), confirmed);

    /// <summary>Arrow keys: move by whole units (one undo step per press).</summary>
    public EditOutcome Nudge(WidgetPackageTree tree, WidgetNode node, double dx, double dy, bool confirmed = false)
    {
        var handles = DragHandles(node);
        if (dx != 0 && !handles.MoveX || dy != 0 && !handles.MoveY)
            return EditOutcome.Refused(SlotEditor.Move(node, Edits, dx, dy).Reason ?? handles.Refusals[DragHandle.Move] ?? "This widget can't move along that axis.");
        return Apply(tree, node, e => SlotEditor.Move(node, e, dx, dy), confirmed);
    }

    /// <summary>A value typed into the inspector: parsed (invariant, dot decimal) and set through SlotEditor.SetField.</summary>
    public EditOutcome SetValue(WidgetPackageTree tree, WidgetNode node, string path, string text, bool confirmed = false)
    {
        var (value, why) = ParseNumber(text);
        return why != null ? EditOutcome.Refused(why) : Apply(tree, node, e => SlotEditor.SetField(node, e, path, value), confirmed);
    }

    /// <summary>Parses a typed number; returns the reason when it isn't one.</summary>
    public static (double Value, string? Refusal) ParseNumber(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return (0, "Type a number.");
        if (t.Contains(',')) return (0, $"\"{t}\": use a dot as the decimal separator (e.g. 146.5).");
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? (v, null) : (0, $"\"{t}\" is not a number.");
    }

    /// <summary>
    /// Puts a package (or every package when null) back to its last save. It is an ordinary edit step, so Undo brings
    /// the discarded edits back. Returns false when there was nothing unsaved.
    /// </summary>
    public bool DiscardUnsaved(string? package = null)
    {
        var any = false;
        foreach (var s in DirtySessions.Where(s => package == null || s.PackagePath.Equals(package, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var keys = s.Current.Values.Select(kv => kv.Key).Union(s.Saved.Values.Select(kv => kv.Key));
            var changes = keys.Select(k => new FieldChange(k, s.Current.TryGet(k, out var c) ? c : s.BaseValue(k)!.Value,
                s.Saved.TryGet(k, out var v) ? v : s.BaseValue(k)!.Value)).ToList();
            if (s.Apply(EditResult.Of(changes)) is { } why) throw new InvalidOperationException(why); // keys come from the session itself
            _undo.Push(s.PackagePath);
            any = true;
        }
        if (!any) return false;
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>The package the next Undo / Redo changes (for the status line).</summary>
    public string? NextUndo => _undo.Count > 0 ? _undo.Peek() : null;
    public string? NextRedo => _redo.Count > 0 ? _redo.Peek() : null;

    /// <summary>Undoes the last edit in the project, whichever package it was in.</summary>
    public bool Undo()
    {
        if (_undo.Count > 0 && _undo.Peek() == TexturesStep)
        {
            _texturesRedo.Push(_textures);
            _textures = _texturesUndo.Pop();
            _redo.Push(_undo.Pop());
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (_undo.Count == 0 || !Session(_undo.Peek()).Undo()) return false;
        _redo.Push(_undo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count > 0 && _redo.Peek() == TexturesStep)
        {
            _texturesUndo.Push(_textures);
            _textures = _texturesRedo.Pop();
            _undo.Push(_redo.Pop());
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (_redo.Count == 0 || !Session(_redo.Peek()).Redo()) return false;
        _undo.Push(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Saves every package with unsaved edits; each result says saved or refused (and why).</summary>
    public IReadOnlyList<(string Package, SaveResult Result)> SaveAll()
    {
        var results = DirtySessions.ToList().Select(s => (s.PackagePath, s.Save())).ToList();
        SaveTextureEdits();
        Changed?.Invoke(this, EventArgs.Empty);
        return results;
    }

    /// <summary>Saves one package (when it has unsaved edits), e.g. before leaving it.</summary>
    public IReadOnlyList<(string Package, SaveResult Result)> SavePackage(string package)
    {
        if (!IsDirty(package)) return [];
        var s = _sessions[package];
        var result = s.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return [(s.PackagePath, result)];
    }

    /// <summary>What the save dialog shows: one line per package, with every refusal rule and message.</summary>
    public static string SaveReport(IReadOnlyList<(string Package, SaveResult Result)> results)
    {
        if (results.Count == 0) return "Nothing to save.";
        var refused = results.Count(r => !r.Result.Saved);
        var head = refused == 0 ? $"Saved {results.Count} package{(results.Count == 1 ? "" : "s")}."
            : $"{refused} of {results.Count} package{(results.Count == 1 ? " was" : "s were")} refused and not written; the previous save is untouched.";
        var lines = results.Select(r => r.Result.Saved
            ? $"{GamePaths.ShortName(r.Package)}: {r.Result.Summary}\n    {r.Result.OutputPath}"
            : $"{GamePaths.ShortName(r.Package)}: REFUSED\n" + string.Join("\n", r.Result.Refusals.Select(x => $"    [{x.Rule}] {x.Message}")));
        return head + "\n\n" + string.Join("\n\n", lines);
    }

    /// <summary>The status line after a save: one entry per package (values, bytes that differ from stock).</summary>
    public static string SaveStatus(IReadOnlyList<(string Package, SaveResult Result)> results)
    {
        if (results.Count == 0) return "Nothing to save.";
        var refused = results.Where(r => !r.Result.Saved).Select(r => GamePaths.ShortName(r.Package)).ToList();
        if (refused.Count > 0) return $"Save refused for {string.Join(", ", refused)}; nothing of {(refused.Count == 1 ? "it" : "them")} was written.";
        var parts = results.Select(r =>
        {
            int values = r.Result.Plan.ValueCount, bytes = r.Result.ChangedOffsets.Count, tags = r.Result.Plan.Insertions.Count;
            return values == 0 ? $"{GamePaths.ShortName(r.Package)} (back to stock)"
                : $"{GamePaths.ShortName(r.Package)} ({values} value{(values == 1 ? "" : "s")}, {bytes} byte{(bytes == 1 ? "" : "s")} differ from stock" +
                  (tags > 0 ? $", {tags} new tag{(tags == 1 ? "" : "s")}: +{r.Result.Plan.EditedSize - r.Result.Plan.StockSize} bytes)" : ")");
        });
        var grew = results.Any(r => r.Result.Plan.ChangesSize);
        return $"Saved {string.Join(", ", parts)}; rebuilt from the stock copy, verified, {(grew ? "re-parsed" : "size unchanged")}.";
    }

    // ---- 6d: bytes to patch ----

    private readonly Dictionary<string, Dictionary<int, string>> _widgetNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bytes-to-patch panel: every package with edits, planned on its stock copy (live: call after each change).</summary>
    public BytesReport BytesToPatch() => BytesReport.Build(Project, _sessions.Values, WidgetOf);

    /// <summary>The widget whose slot is this export (the tree is cached by the loader), or null.</summary>
    private string? WidgetOf(string package, int slotExport)
    {
        if (!_widgetNames.TryGetValue(package, out var names))
        {
            try
            {
                names = _loadTree(package).All().Where(n => n.Slot != null && !n.IsReadOnly)
                    .GroupBy(n => n.Slot!.ExportIndex).ToDictionary(g => g.Key, g => g.First().Name);
            }
            catch (Exception) { names = []; } // a tree that doesn't load only costs the widget name (the slot name is shown)
            _widgetNames[package] = names;
        }
        return names.GetValueOrDefault(slotExport);
    }

    // ---- step 3: texture edits ----

    public TextureUsage TextureUsage => _textureUsage.Value;
    private TextureFormats? _textureFormats;
    /// <summary>Every UI texture's format (clone sources), built once per game and cached in work/cache.</summary>
    public TextureFormats TextureFormats => _textureFormats ??= TextureFormats.LoadOrBuild(_source, UiAssetIndex.LoadOrBuild(_source));
    /// <summary>The project's texture edits as they are now (textures.json plus unsaved changes).</summary>
    public IReadOnlyList<TextureEdit> TextureEdits => _textures;
    public bool TexturesDirty => !_textures.SequenceEqual(_texturesSaved);

    /// <summary>The texture a widget names (a uiImage's stored soft path), or null.</summary>
    public TextureRef? TextureOf(string package, string widget) => TextureUsage.Of(package, widget) is { IsImageTexture: true } r ? r : null;

    /// <summary>Every stored reference to a texture, in any package.</summary>
    public IReadOnlyList<TextureRef> TextureUsers(string texture) => TextureUsage.UsersOf(texture);

    /// <summary>The packages that show a package: it embedded somewhere, directly or through others.</summary>
    public IReadOnlyCollection<string> EmbeddedBy(string package) => _usage.AllUsers(package);

    /// <summary>
    /// The inspector's Texture section for a uiImage widget (null for any other widget): its texture, every widget naming
    /// it with where each is shown, clone sources when it can't be cloned, and its edit in the project.
    /// </summary>
    public TextureInspection? InspectTexture(WidgetNode node)
    {
        if (node.ClassName != "uiImage_C" || TextureOf(node.PackagePath, node.Name) is not { } current) return null;
        var tex = Modding.TextureEdits.LoadTexture(_source, current.Texture);
        var refusal = Modding.TextureEdits.SourceRefusal(_source, current.Texture);
        var users = TextureUsers(current.Texture).Select(u =>
        {
            var shown = _usage.AllUsers(u.Package);
            return new TextureUserRow
            {
                Package = u.Package, Widget = u.Export, What = $"{u.ExportClass}.{u.Property}",
                IsThis = u.Package.Equals(node.PackagePath, StringComparison.OrdinalIgnoreCase) && u.Export == node.Name,
                SamePackage = u.Package.Equals(node.PackagePath, StringComparison.OrdinalIgnoreCase) && u.Export != node.Name && u.NameMapPath == current.NameMapPath,
                IsOnline = u.Package.Contains("/MatchTimeConnection/", StringComparison.OrdinalIgnoreCase),
                ShownIn = shown.Count == 0 ? "" : $"shown in {shown.Count}: {string.Join(", ", shown.Select(GamePaths.ShortName).Order(StringComparer.OrdinalIgnoreCase))}",
            };
        }).OrderByDescending(r => r.IsThis).ThenByDescending(r => r.SamePackage).ThenBy(r => r.IsOnline).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
        return new TextureInspection
        {
            Ref = current,
            Width = tex?.PlatformData.SizeX ?? 0, Height = tex?.PlatformData.SizeY ?? 0,
            Format = tex?.PlatformData.PixelFormat ?? "not a Texture2D", Mips = tex?.PlatformData.Mips.Length ?? 0,
            ThemeCopies = Modding.TextureEdits.ThemeCopies(_source, current.Texture),
            CloneRefusal = refusal,
            Sources = refusal == null ? [] : Modding.TextureEdits.Sources(_source, TextureFormats, current),
            Margins = Layout.PreviewRaster.StoredMargins(node),
            Users = users,
            Blocked = GamePaths.IsLibrary(node.PackagePath) ? "This image belongs to a library widget, shared by the whole game: replace it in the widget that uses it." : null,
            Edit = TextureEditOf(node.PackagePath, node.Name),
        };
    }

    private readonly Dictionary<string, DecodedImage?> _overrideImages = new();

    /// <summary>
    /// What a preview draws for texture edits (default: the current ones) plus an optional trial (an import not applied
    /// yet): each edit's image on the boxes of the packages it re-points. An edit whose image can't be read is left out.
    /// </summary>
    public TextureOverrides OverridesOf(IReadOnlyList<TextureEdit>? edits = null, (TextureEdit Edit, DecodedImage Image)? trial = null)
    {
        var list = new List<TextureOverride>();
        foreach (var e in (edits ?? _textures).Where(e => trial == null || !(e.Package == trial.Value.Edit.Package && e.Widget == trial.Value.Edit.Widget)))
            if (ImageOf(e) is { } image)
                list.Add(new TextureOverride(e.Texture, StoringPackages(e).ToHashSet(StringComparer.OrdinalIgnoreCase), image, e.MarginsZero, Widget: OnlyWidget(e)));
        if (trial is var (t, img))
            list.Add(new TextureOverride(t.Texture, StoringPackages(t).ToHashSet(StringComparer.OrdinalIgnoreCase), img, t.MarginsZero, IsTrial: true, Widget: OnlyWidget(t)));
        return new TextureOverrides(list);

        static string? OnlyWidget(TextureEdit e) => e.Scope == TextureEditScope.Widget ? e.Widget : null;
    }

    /// <summary>An edit's pixels as the clone's mip 0 will hold them (the imported PNG, or the stock pixels whitened).</summary>
    public DecodedImage? ImageOf(TextureEdit e)
    {
        var key = e.Mode == TextureEditMode.White ? "white:" + e.SourceTexture : $"{ProjectFolder}|{e.Image}|{e.ImageSha256}";
        lock (_overrideImages)
        {
            if (_overrideImages.TryGetValue(key, out var cached)) return cached;
            DecodedImage? image = null;
            if (e.Mode == TextureEditMode.Image) image = TextureEditStore.ReadImage(ProjectFolder, e).Image;
            else if (Modding.TextureEdits.LoadTexture(_source, e.SourceTexture) is { PlatformData.PixelFormat: "PF_B8G8R8A8" } t && t.PlatformData.Mips[0].BulkData?.Data is { } data)
            {
                var white = data.ToArray();
                for (int i = 0; i < white.Length; i += 4) white[i] = white[i + 1] = white[i + 2] = 255;
                image = new DecodedImage(t.PlatformData.Mips[0].SizeX, t.PlatformData.Mips[0].SizeY, white, "white");
            }
            return _overrideImages[key] = image;
        }
    }

    public TextureEdit? TextureEditOf(string package, string widget) =>
        _textures.FirstOrDefault(e => e.Package.Equals(package, StringComparison.OrdinalIgnoreCase) && e.Widget == widget);

    /// <summary>
    /// A new texture edit for a widget (not applied yet): the PNG is copied into the project's _art folder (named by its
    /// sha256, so importing never changes an existing edit), the name defaults to a free same-length one.
    /// </summary>
    public (TextureEdit? Edit, DecodedImage? Image, string? Why) PrepareTextureEdit(string package, string widget, TextureEditMode mode,
        string? png = null, string? newName = null, string? source = null, bool marginsZero = false, TextureEditScope scope = TextureEditScope.Widget)
    {
        if (TextureOf(package, widget) is not { } current) return (null, null, $"{widget} in {GamePaths.ShortName(package)} isn't a uiImage with a stored texture");
        string? relative = null, sha = null;
        DecodedImage? image = null;
        if (mode == TextureEditMode.Image)
        {
            if (png == null) return (null, null, "pick a PNG image");
            string? why;
            (relative, sha, image, why) = TextureEditStore.Import(_guard, ProjectFolder, png);
            if (relative == null) return (null, null, why);
        }
        var sourceTexture = source ?? current.Texture;
        var taken = _textures.Where(e => !(e.Package == package && e.Widget == widget)).Select(e => e.NewName);
        newName ??= Modding.TextureEdits.SuggestName(_source, sourceTexture, taken);
        return (new TextureEdit(package, widget, current.Texture, mode, newName, scope, relative, sha, source, marginsZero), image, null);
    }

    /// <summary>
    /// Plans texture edits (default: the current ones) on top of each package's saved edit-set chunk, so the result is
    /// what an export ships.
    /// </summary>
    public TexturePlan PlanTextures(IReadOnlyList<TextureEdit>? edits = null)
    {
        edits ??= _textures;
        // the export dialog re-plans on every keystroke: the same edits on the same saved edit sets give the same plan
        var inputs = edits.SelectMany(StoringPackages).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => _sessions.TryGetValue(p, out var s) && s.Saved.Count > 0 ? (object) s.Saved : null).ToList();
        if (_texturePlan is { } cached && cached.Edits.SequenceEqual(edits) && cached.Inputs.SequenceEqual(inputs, ReferenceEqualityComparer.Instance)
            && cached.Folder == ProjectFolder)
            return cached.Plan;
        var plan = PlanTextures(edits, SavedChunks(edits));
        _texturePlan = (edits.ToList(), inputs, ProjectFolder, plan);
        return plan;
    }

    private (List<TextureEdit> Edits, List<object?> Inputs, string Folder, TexturePlan Plan)? _texturePlan;

    /// <summary>
    /// Plans texture edits on given saved chunks (no session access, so it may run in the background: take the chunks
    /// with <see cref="SavedChunks"/> on the editor's thread).
    /// </summary>
    public TexturePlan PlanTextures(IReadOnlyList<TextureEdit> edits, IReadOnlyDictionary<string, byte[]> saved) =>
        Modding.TextureEdits.Plan(_source, TextureUsage, ProjectFolder, edits, p => saved.GetValueOrDefault(p), p => _usage.AllUsers(p));

    /// <summary>The saved edit-set chunk of every package the edits re-point that has one (call on the editor's thread).</summary>
    public IReadOnlyDictionary<string, byte[]> SavedChunks(IEnumerable<TextureEdit> edits)
    {
        var chunks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in edits.SelectMany(StoringPackages).Distinct(StringComparer.OrdinalIgnoreCase))
            if (_sessions.TryGetValue(p, out var s) && s.Saved.Count > 0 && s.ReadSavedChunk().Chunk is { } chunk) chunks[p] = chunk;
        return chunks;
    }

    /// <summary>
    /// Adds a texture edit (or replaces the widget's earlier one) after planning it: a refusal of the tools refuses it.
    /// The first texture edit of a package asks first (it lists who shows the change). One undo step.
    /// </summary>
    public EditOutcome ApplyTextureEdit(TextureEdit edit, bool confirmed = false)
    {
        var others = _textures.Where(e => !(e.Package.Equals(edit.Package, StringComparison.OrdinalIgnoreCase) && e.Widget == edit.Widget)).ToList();
        var next = others.Append(edit).ToList();
        var plan = PlanTextures(next);
        if (!plan.Ok) return EditOutcome.Refused(string.Join("\n", plan.Refusals));
        if (next.SequenceEqual(_textures)) return new EditOutcome(false, null, null, []);
        var info = plan.Items[^1];
        var firstEdit = _gate.NeedsConfirmation(edit.Package) && !_textures.Any(e => e.Package.Equals(edit.Package, StringComparison.OrdinalIgnoreCase))
            && !(_sessions.TryGetValue(edit.Package, out var s) && (s.Current.Count > 0 || s.Saved.Count > 0));
        if (firstEdit && !confirmed)
            return new EditOutcome(false, null, TextureConfirmation(info), []);
        _gate.Confirm(edit.Package);
        PushTextures(next);
        return new EditOutcome(true, null, null, info.Warnings.Select(w => new EditWarning(WarningKind.SharedTexture, Severity.Info, w)).ToList());
    }

    /// <summary>Removes a widget's texture edit (one undo step). False when it has none.</summary>
    public bool RemoveTextureEdit(string package, string widget)
    {
        if (TextureEditOf(package, widget) is not { } edit) return false;
        PushTextures(_textures.Where(e => !ReferenceEquals(e, edit)).ToList());
        return true;
    }

    /// <summary>Writes textures.json when the texture edits changed since the last save. Returns whether it wrote.</summary>
    public bool SaveTextureEdits()
    {
        if (!TexturesDirty) return false;
        TextureEditStore.Save(_guard, ProjectFolder, _textures);
        _texturesSaved = _textures;
        return true;
    }

    private void PushTextures(IReadOnlyList<TextureEdit> next)
    {
        _texturesUndo.Push(_textures);
        _textures = next;
        _texturesRedo.Clear();
        _undo.Push(TexturesStep);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string TextureConfirmation(TextureEditInfo info)
    {
        var e = info.Edit;
        var lines = new List<string>
        {
            $"Replace {GamePaths.ShortName(e.Texture)} on {e.Widget} ({GamePaths.ShortName(e.Package)}) with {(e.Mode == TextureEditMode.White ? "a white copy" : $"your image ({info.Width}x{info.Height})")}?",
            $"New texture package{(info.ClonePaths.Count == 1 ? "" : "s")}: {string.Join(", ", info.ClonePaths.Select(p => p[(p.IndexOf("/Widget/", StringComparison.Ordinal) + 1)..]))}.",
            $"Re-pointed: {string.Join(", ", info.Storing.Select(GamePaths.ShortName))}.",
        };
        lines.AddRange(info.Warnings);
        return string.Join("\n", lines);
    }

    // ---- impact and mods ----

    /// <summary>Every package the current edits reach (edited + embedding packages), stock vs edited.</summary>
    public IReadOnlyList<PackageImpact> Impact() => new EditImpact(_engine, _usage, _width, _height).Predict(Edits, _loadTree);

    /// <summary>
    /// The impact panel for an edit set (take <see cref="Edits"/> on the caller's thread; this only lays out, so it may
    /// run in the background): every package reached, its changed boxes in px and units, and its pending-edit warnings.
    /// </summary>
    public ImpactReport ImpactOf(EditSet edits) =>
        ImpactReport.Build(Project, _width, _height, new EditImpact(_engine, _usage, _width, _height).Predict(edits, _loadTree));

    // ---- 6g: export ----

    /// <summary>The project's packages with saved edits (what the export dialog lists).</summary>
    public IReadOnlyList<ExportCandidate> ExportCandidates()
    {
        var withEdits = _sessions.Values.Where(s => s.Saved.Count > 0 || s.IsDirty).Select(s => s.PackagePath);
        var textured = _textures.SelectMany(StoringPackages);
        return withEdits.Union(textured, StringComparer.OrdinalIgnoreCase)
            .OrderBy(GamePaths.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ExportCandidate(p, _sessions.TryGetValue(p, out var s) ? s.Saved.Count : 0, IsDirty(p),
                _textures.Count(e => StoringPackages(e).Contains(p, StringComparer.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>The packages a texture edit re-points: the picked widget's, or every package naming the texture.</summary>
    public IReadOnlyList<string> StoringPackages(TextureEdit edit)
    {
        if (edit.Scope != TextureEditScope.AllUsers) return [edit.Package];
        var from = TextureUsage.Of(edit.Package, edit.Widget)?.NameMapPath;
        return TextureUsage.UsersOf(edit.Texture).Where(r => r.NameMapPath == from).Select(r => r.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Checks an export and reads what it ships (call on the editor's thread). Refused while anything in the project is
    /// unsaved, for a bad mod or container name (it must end in _P), no package, the same package twice, a package
    /// without saved edits (or whose saved file no longer matches its edits), or a missing retoc or .pak stub.
    /// </summary>
    public ExportPlan PlanExport(ExportRequest request)
    {
        var refusals = new List<string>();
        var warnings = new List<string>();
        var mods = WriteGuard.ForMods(_source.Config);
        var name = request.ModName.Trim();
        var outDir = Path.Combine(mods.Root, name);
        var retoc = RetocInfo.Of(request.RetocPath);

        if (AnyDirty) refusals.Add($"Project {Project} has unsaved edits in {UnsavedPackagesText}. Save (or undo) them first: a mod is built from the saved files.");
        if (ModBuilder.ModNameRefusal(name) is { } badName) refusals.Add(badName);
        else if (mods.Refusal(outDir) is { } outside) refusals.Add(outside);
        if (ModBuilder.ContainerNameRefusal(request.ContainerName.Trim()) is { } badContainer) refusals.Add(badContainer);
        if (!retoc.Exists) refusals.Add($"retoc not found at \"{request.RetocPath}\" (set the retoc path).");
        else if (!retoc.IsTested) warnings.Add($"This retoc (sha256 {retoc.Sha256[..16]}…) is not the tested build {ModBuilder.TestedRetocSha256[..16]}… (app/README.md); the mod is still verified after packing.");
        if (!File.Exists(_source.Config.ModStubPath)) refusals.Add($"The .pak stub {_source.Config.ModStubPath} doesn't exist (setting ModStubPak).");
        if (request.Packages.Count == 0) refusals.Add("Pick at least one package with saved edits.");
        foreach (var dup in request.Packages.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            refusals.Add($"{GamePaths.ShortName(dup.Key)} is picked {dup.Count()} times: a mod carries one edit of a package (its saved edits are already all in one file).");

        var packages = new List<ModPackage>();
        var edits = new List<ExportEdit>();
        var saved = EditSet.Empty;
        // texture edits ship with the packages they re-point (all of them, so a picked edit is never half shipped), plus their clones
        var picked = request.Packages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var textureEdits = _textures.Where(e => StoringPackages(e).Any(p => picked.Contains(p, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var e in textureEdits)
            foreach (var missing in StoringPackages(e).Where(p => !picked.Contains(p, StringComparer.OrdinalIgnoreCase)))
                refusals.Add($"The texture edit on {e.Widget} ({GamePaths.ShortName(e.Package)}) also re-points {GamePaths.ShortName(missing)}: pick it too.");
        var textures = textureEdits.Count > 0 && !AnyDirty ? PlanTextures(textureEdits) : null;
        if (textures != null) refusals.AddRange(textures.Refusals);
        var textured = textureEdits.SelectMany(StoringPackages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in picked)
        {
            var hasEdits = _sessions.TryGetValue(path, out var s) && s.Saved.Count > 0;
            if (!hasEdits && !textured.Contains(path))
            {
                refusals.Add($"{GamePaths.ShortName(path)} has no saved edits in project {Project}.");
                continue;
            }
            if (AnyDirty) continue; // refused above: a refused plan carries nothing to ship
            if (textures?.Packages.FirstOrDefault(p => p.PackagePath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } repointed)
                packages.Add(repointed); // its saved edit-set chunk plus the re-point layers, checked against stock
            if (!hasEdits) continue;
            var (chunk, why) = s!.ReadSavedChunk();
            if (chunk == null) { refusals.Add(why!); continue; }
            if (!textured.Contains(path)) packages.Add(new ModPackage(s.PackagePath, s.FileKey, s.ChunkIdHex, chunk));
            saved = saved.With(s.Saved.Values.Select(kv => new FieldChange(kv.Key, s.BaseValue(kv.Key) ?? double.NaN, kv.Value)), _ => null);
            string Slot(int export, string slotName) => WidgetOf(s.PackagePath, export) is { } w ? $"{w} ({slotName})" : slotName;
            var plan = s.Plan();
            edits.AddRange(plan.Entries.Select(e => new ExportEdit(GamePaths.ShortName(s.PackagePath), Slot(e.Key.SlotExport, e.SlotName),
                e.Key.Path.Replace("LayoutData.", ""), e.OldValue, e.NewValue, e.OffsetHex, e.OldHex, e.NewHex)));
            edits.AddRange(plan.AnimWrites.Select(w => new ExportEdit(GamePaths.ShortName(s.PackagePath), w.Description, w.Copy == "range" ? "animation range bound" : $"animation key, {w.Copy} copy",
                w.OldValue, w.NewValue, $"{w.OffsetHex} ({w.WindowText})", w.OldHex, w.NewHex)));
            edits.AddRange(plan.Insertions.Select(i => new ExportEdit(GamePaths.ShortName(s.PackagePath), Slot(i.ExportIndex, i.SlotName),
                i.Path.Replace("LayoutData.", "") + " (new tag)", i.Key is { } k ? s.BaseValue(k) ?? 0 : 0, i.Value, $"{i.InsertAtHex} (inserted before {i.Before}, +{TagInserter.FloatTagSize} bytes)", "(not stored)", i.TagHex)));
        }
        if (textures is { Ok: true }) packages.AddRange(textures.Clones);
        var impactWarnings = saved.Count == 0 ? [] : ExportWarning.From(ExportImpact(saved));
        var unticked = impactWarnings.Where(w => w.IsDanger && !request.IsAcknowledged(w)).ToList();
        if (unticked.Count > 0)
            refusals.Add($"{(unticked.Count == 1 ? "A danger warning is" : $"{unticked.Count} danger warnings are")} not acknowledged " +
                         $"({(unticked.Count == 1 ? "tick it below" : "tick each below")}; umgmod build: --ack-danger): {string.Join("; ", unticked.Select(w => w.Package + ": " + w.Warning.Message))}");
        if (refusals.Count == 0 && Directory.Exists(outDir))
            warnings.Add($"{outDir} exists and will be replaced.");
        return new ExportPlan(request with { ModName = name, ContainerName = request.ContainerName.Trim() }, Project, outDir, retoc, refusals, warnings, packages, saved, edits, impactWarnings)
        {
            Textures = textures?.Items ?? [],
            ExpectedTextures = textures?.Expected ?? [],
        };
    }

    private (string Key, ImpactReport Report)? _exportImpact;

    /// <summary>The impact of the saved edits being shipped; cached, since the dialog re-plans on every keystroke.</summary>
    private ImpactReport ExportImpact(EditSet saved)
    {
        var key = string.Join("\n", saved.Values.Select(kv => $"{kv.Key}={kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal));
        if (_exportImpact is { } cached && cached.Key == key) return cached.Report;
        var report = ImpactOf(saved);
        _exportImpact = (key, report);
        return report;
    }

    /// <summary>
    /// Builds a checked plan (no session access, so it may run in the background): ModBuilder packs and verifies the
    /// container (a failed retoc verify or read-back aborts and removes mod\), then the stock vs edited layouts of every
    /// package the saved edits reach go to predictions\ and the test instructions next to mod\. Writes only under
    /// work\out\mods\&lt;name&gt;.
    /// </summary>
    public ExportResult RunExport(ExportPlan plan, IRetoc retoc, IProgress<string>? log = null)
    {
        if (!plan.Ok) return new ExportResult(false, plan.Refusals, plan.OutDir, [], "", null, [], null);
        log?.Report($"Building {plan.Request.ModName} ({plan.Request.ContainerName}) from {plan.Packages.Count} package{(plan.Packages.Count == 1 ? "" : "s")}: {string.Join(", ", plan.Packages.Select(p => GamePaths.ShortName(p.PackagePath)))}");
        var build = new ModBuilder(_source, new LoggingRetoc(retoc, log)).Build(plan.Request.ModName, plan.Request.ContainerName, plan.Packages);
        if (!build.Built)
        {
            log?.Report("FAILED: " + string.Join(" | ", build.Refusals));
            log?.Report(Directory.Exists(build.ModDir) ? $"{build.ModDir} is still there" : $"{build.ModDir} removed; see FAILED.txt");
            return new ExportResult(false, build.Refusals, build.OutDir, [], build.Log, null, [], null);
        }
        foreach (var f in build.Files) log?.Report($"  {f.Name}  {f.Size:N0} bytes  sha256 {f.Sha256}");
        if (plan.ExpectedTextures.Count > 0)
        {
            log?.Report($"Mounting the mod on top of the game: {plan.ExpectedTextures.Count} new texture package(s) must load by path and decode to the imported pixels…");
            if (TextureMountCheck(plan, build.ModDir) is { } problem)
            {
                var guard = WriteGuard.ForMods(_source.Config);
                guard.DeleteDirectory(build.ModDir);
                guard.WriteAllText(Path.Combine(plan.OutDir, "FAILED.txt"), problem + Environment.NewLine + build.Log);
                log?.Report("FAILED: " + problem);
                return new ExportResult(false, [problem], build.OutDir, [], build.Log, null, [], null);
            }
        }

        log?.Report("Laying out every package the saved edits reach (stock vs edited)…");
        var mods = WriteGuard.ForMods(_source.Config);
        var impacts = new EditImpact(_engine, _usage, _width, _height).Predict(plan.SavedEdits, _loadTree);
        var predictions = Path.Combine(plan.OutDir, "predictions");
        var files = new List<string>();
        foreach (var i in impacts.Where(i => i.Error == null))
        {
            foreach (var (suffix, layout) in new[] { (".stock.json", i.Stock), (".json", i.Edited) })
            {
                var file = Path.Combine(predictions, i.Package.Name + suffix);
                mods.WriteAllText(file, LayoutExport.ToJson(layout, i.Package.PackagePath));
                files.Add(file);
            }
        }
        var report = ImpactReport.Build(Project, _width, _height, impacts);
        foreach (var w in ExportWarning.From(report))
            log?.Report($"  {w.Text}{(w.IsDanger ? " (acknowledged)" : "")}");
        var instructions = Path.Combine(plan.OutDir, ModInstructions.FileName);
        mods.WriteAllText(instructions, ModInstructions.Build(plan, build, report, p => _usage.DirectUsers(p).Count == 0, DateTime.Now, p => _usage.AllUsers(p)));
        log?.Report($"{files.Count} prediction files in {predictions}");
        log?.Report($"Test instructions: {instructions}");
        log?.Report($"Done. Copy the {build.Files.Count} files in {build.ModDir} to the device. {ModInstructions.OnlineWarning}.");
        return new ExportResult(true, [], plan.OutDir, build.Files, build.Log, instructions, files, report);
    }

    /// <summary>
    /// With the built mod mounted on top of the stock game (as the canaries were checked): every new texture package loads by
    /// path and its mip 0 is the imported pixels, and every re-pointed package names its new texture. Returns the problem, or null.
    /// </summary>
    private string? TextureMountCheck(ExportPlan plan, string modDir)
    {
        try
        {
            using var withMod = GameSource.Open(_source.Config with { ExtraPaksDirs = [modDir] });
            foreach (var (path, mip0) in plan.ExpectedTextures)
            {
                var tex = withMod.LoadPackage(path).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D>().SingleOrDefault();
                if (tex == null || !(tex.PlatformData.Mips[0].BulkData?.Data ?? []).AsSpan().SequenceEqual(mip0))
                    return $"mounted: {path} {(tex == null ? "doesn't load" : "doesn't decode to the imported pixels")}";
            }
            foreach (var item in plan.Textures)
                foreach (var package in item.Storing)
                {
                    var named = withMod.LoadPackage(package).GetExports().Select(WidgetTreeReader.StoredValues)
                        .Count(v => v.Values.Any(x => TextureUsage.NameMapForm(x) == item.To));
                    if (named == 0) return $"mounted: no widget in {GamePaths.ShortName(package)} names {item.To}";
                }
            return null;
        }
        catch (Exception e) { return $"mounted: the game with the mod can't be read ({e.GetType().Name}: {e.Message})"; }
    }

    /// <summary>Packs the saved edits of every package in the project. Refused while anything is unsaved.</summary>
    public ModResult BuildMod(string modName, string containerName, IRetoc retoc)
    {
        var problems = new List<string>();
        var packages = new List<ModPackage>();
        if (AnyDirty) problems.Add("Save (or undo) the unsaved edits first.");
        foreach (var s in _sessions.Values.Where(s => s.Saved.Count > 0))
        {
            var (chunk, why) = s.ReadSavedChunk();
            if (chunk == null) problems.Add(why!);
            else packages.Add(new ModPackage(s.PackagePath, s.FileKey, s.ChunkIdHex, chunk));
        }
        if (packages.Count == 0 && problems.Count == 0) problems.Add($"Project {Project} has no saved edits.");
        return problems.Count > 0
            ? new ModResult(false, problems, Path.Combine(_source.Config.ModsDir, modName), [], "")
            : new ModBuilder(_source, retoc).Build(modName, containerName, packages);
    }
}
