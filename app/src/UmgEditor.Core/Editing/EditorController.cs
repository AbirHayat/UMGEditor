using System.Text.Json;
using System.Text.RegularExpressions;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Editing;

/// <summary>Why a field (or a whole slot) is greyed out, grouped so the UI can explain each kind once.</summary>
public enum GreyReason { None, Library, EmbeddedReadOnly, NoSlot, NotCanvasSlot, NotStored, Boolean, NotLayoutValue }

/// <summary>One slot field as the inspector shows it: stock value, whether it's stored or edited, and why it may be greyed.</summary>
public sealed record FieldInspection(string Path, string StockValue, bool Stored, double? EditedValue, GreyReason Grey, string? Reason)
{
    public bool Editable => Grey == GreyReason.None;
    public bool IsEdited => EditedValue != null;
    public string State => IsEdited ? "edited" : Stored ? "stored" : "default";
    public string DisplayValue => EditedValue is { } v ? SlotEditor.Format(v) : StockValue;
    public string ShortPath => Path.Replace("LayoutData.", "");
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
        if (AnyDirty) return $"Project {Project} has unsaved edits in {string.Join(", ", DirtySessions.Select(s => GamePaths.ShortName(s.PackagePath)))}. Save or undo them first.";
        Project = name;
        _sessions.Clear();
        _undo.Clear();
        _redo.Clear();
        _gate = new FirstEditGate();
        LoadProjectSessions();
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
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
        if (result.Changes.Count == 0) return new EditOutcome(false, null, null, []);

        var firstEdit = _gate.NeedsConfirmation(node.PackagePath) && session.Current.Count == 0 && session.Saved.Count == 0;
        if (firstEdit && !confirmed)
            return new EditOutcome(false, null, new EditWarnings(_engine, _usage, _width, _height).ForSelection(tree, node).ConfirmationText, []);

        var after = Edits.With(result.Changes, session.StockValue);
        var pending = new EditWarnings(_engine, _usage, _width, _height).ForPendingEdit(tree, node, after);
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

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

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

    // ---- impact and mods ----

    /// <summary>Every package the current edits reach (edited + embedding packages), stock vs edited.</summary>
    public IReadOnlyList<PackageImpact> Impact() => new EditImpact(_engine, _usage, _width, _height).Predict(Edits, _loadTree);

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
