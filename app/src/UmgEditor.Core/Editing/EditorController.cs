using System.Text.Json;
using System.Text.RegularExpressions;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;

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
    public string State => IsEdited ? "edited" : Stored ? "stored" : "default";
    public string DisplayValue => EditedValue is { } v ? SlotEditor.Format(v) : StockValue;
    public string ShortPath => Path.Replace("LayoutData.", "");
    /// <summary>Why it's greyed, or the stock value when edited.</summary>
    public string? ToolTip => Reason ?? (IsEdited ? $"Stock value {StockValue}. Type it again (or undo) to go back." : null);

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

    public EditorController(GameSource source, UsageGraph usage, LayoutEngine engine, Func<string, WidgetPackageTree> loadTree,
        int viewportWidth = 1786, int viewportHeight = 1000, string project = DefaultProject)
    {
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
    public bool AnyDirty => _sessions.Values.Any(s => s.IsDirty);
    public bool IsDirty(string package) => _sessions.TryGetValue(package, out var s) && s.IsDirty;
    /// <summary>"MatchTimeBase, MatchTime": the packages with unsaved edits, for prompts.</summary>
    public string UnsavedPackagesText => string.Join(", ", DirtySessions.Select(s => GamePaths.ShortName(s.PackagePath)));

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
        return new NodeInspection(node, slotReason, slotGrey, fields, report);
    }

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
        GreyReason.NotStored => "Not stored in the file: the engine default applies. Changing it needs a new property tag, which would change the file size (in-place patches only).",
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
        var after = previous.With(result.Changes, session.StockValue);
        var pending = new EditWarnings(_engine, _usage, _width, _height).ForPendingEdit(tree, node, after, previous);
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
            var changes = keys.Select(k => new FieldChange(k, s.Current.TryGet(k, out var c) ? c : s.StockValue(k)!.Value,
                s.Saved.TryGet(k, out var v) ? v : s.StockValue(k)!.Value)).ToList();
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
        if (_undo.Count == 0 || !Session(_undo.Peek()).Undo()) return false;
        _redo.Push(_undo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0 || !Session(_redo.Peek()).Redo()) return false;
        _undo.Push(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Saves every package with unsaved edits; each result says saved or refused (and why).</summary>
    public IReadOnlyList<(string Package, SaveResult Result)> SaveAll()
    {
        var results = DirtySessions.ToList().Select(s => (s.PackagePath, s.Save())).ToList();
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
            int values = r.Result.Plan.Entries.Count, bytes = r.Result.ChangedOffsets.Count;
            return values == 0 ? $"{GamePaths.ShortName(r.Package)} (back to stock)"
                : $"{GamePaths.ShortName(r.Package)} ({values} value{(values == 1 ? "" : "s")}, {bytes} byte{(bytes == 1 ? "" : "s")} differ from stock)";
        });
        return $"Saved {string.Join(", ", parts)}; rebuilt from the stock copy, verified, size unchanged.";
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
    public IReadOnlyList<ExportCandidate> ExportCandidates() =>
        _sessions.Values.Where(s => s.Saved.Count > 0 || s.IsDirty)
            .OrderBy(s => GamePaths.ShortName(s.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(s => new ExportCandidate(s.PackagePath, s.Saved.Count, s.IsDirty)).ToList();

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
        foreach (var path in request.Packages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_sessions.TryGetValue(path, out var s) || s.Saved.Count == 0)
            {
                refusals.Add($"{GamePaths.ShortName(path)} has no saved edits in project {Project}.");
                continue;
            }
            if (AnyDirty) continue; // refused above: a refused plan carries nothing to ship
            var (chunk, why) = s.ReadSavedChunk();
            if (chunk == null) { refusals.Add(why!); continue; }
            packages.Add(new ModPackage(s.PackagePath, s.FileKey, s.ChunkIdHex, chunk));
            saved = saved.With(s.Saved.Values.Select(kv => new FieldChange(kv.Key, s.StockValue(kv.Key) ?? double.NaN, kv.Value)), _ => null);
            edits.AddRange(s.Plan().Entries.Select(e => new ExportEdit(GamePaths.ShortName(s.PackagePath), WidgetOf(s.PackagePath, e.Key.SlotExport) is { } w ? $"{w} ({e.SlotName})" : e.SlotName,
                e.Key.Path.Replace("LayoutData.", ""), e.OldValue, e.NewValue, e.OffsetHex, e.OldHex, e.NewHex)));
        }
        var impactWarnings = saved.Count == 0 ? [] : ExportWarning.From(ExportImpact(saved));
        var unticked = impactWarnings.Where(w => w.IsDanger && !request.IsAcknowledged(w)).ToList();
        if (unticked.Count > 0)
            refusals.Add($"Tick {(unticked.Count == 1 ? "the danger warning" : $"each of the {unticked.Count} danger warnings")} below to acknowledge " +
                         $"{(unticked.Count == 1 ? "it" : "them")}: {string.Join("; ", unticked.Select(w => w.Package + ": " + w.Warning.Message))}");
        if (refusals.Count == 0 && Directory.Exists(outDir))
            warnings.Add($"{outDir} exists and will be replaced.");
        return new ExportPlan(request with { ModName = name, ContainerName = request.ContainerName.Trim() }, Project, outDir, retoc, refusals, warnings, packages, saved, edits, impactWarnings);
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
        mods.WriteAllText(instructions, ModInstructions.Build(plan, build, report, p => _usage.DirectUsers(p).Count == 0, DateTime.Now));
        log?.Report($"{files.Count} prediction files in {predictions}");
        log?.Report($"Test instructions: {instructions}");
        log?.Report($"Done. Copy the {build.Files.Count} files in {build.ModDir} to the device. {ModInstructions.OnlineWarning}.");
        return new ExportResult(true, [], plan.OutDir, build.Files, build.Log, instructions, files, report);
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
