using System.Diagnostics;
using System.IO;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;

namespace UmgEditor.App.ViewModels;

/// <summary>A package with saved edits, ticked to go into the mod.</summary>
public sealed class PackageChoice(ExportCandidate candidate, Action changed) : ObservableObject
{
    public string PackagePath => candidate.PackagePath;
    public string Name => candidate.Name;
    public string Note => candidate.Dirty ? "unsaved changes: save first"
        : string.Join(" + ", new[]
        {
            candidate.SavedEdits > 0 ? $"{candidate.SavedEdits} saved edit{(candidate.SavedEdits == 1 ? "" : "s")}" : null,
            candidate.TextureEdits > 0 ? $"{candidate.TextureEdits} texture{(candidate.TextureEdits == 1 ? "" : "s")}" : null,
        }.OfType<string>());

    private bool _checked = !candidate.Dirty;
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) changed(); } }
}

/// <summary>An Impact warning for the edits being shipped; a Danger one has a tick box that Build waits for.</summary>
public sealed class ImpactWarningRow(ExportWarning warning, int index, bool acknowledged, Action<ImpactWarningRow> changed) : ObservableObject
{
    public ExportWarning Warning => warning;
    public string Text => warning.Text;
    public bool IsDanger => warning.IsDanger;
    public string Severity => warning.Warning.Severity.ToString();
    public string TextId => $"ExportImpactWarning_{index}";
    public string AckId => $"ExportAck_{index}";

    private bool _acknowledged = acknowledged;
    public bool IsAcknowledged { get => _acknowledged; set { if (Set(ref _acknowledged, value)) changed(this); } }
}

/// <summary>A built file as a list row (a class: list rows must not be equal records, see WarningView).</summary>
public sealed class FileRow(ModFile file)
{
    public string Name => file.Name;
    public string Size => $"{file.Size:N0} B";
    public string Sha256 => file.Sha256;
}

/// <summary>
/// 6g: the export dialog. Every input change re-plans through EditorController.PlanExport (refusals and warnings
/// shown live, Build only when nothing is refused); Build runs RunExport in the background and streams its log.
/// The retoc path is a setting: it is saved to umgeditor.json when a build starts. The Impact panel's warnings for the
/// picked edits are listed; each Danger one needs its tick (the plan refuses until then).
/// </summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly EditorController _controller;
    private readonly Action<string> _saveRetocPath;

    public ExportViewModel(EditorController controller, string retocPath, Action<string> saveRetocPath)
    {
        _controller = controller;
        _saveRetocPath = saveRetocPath;
        _retocPath = retocPath;
        _modName = controller.Project == EditorController.DefaultProject ? "mod1" : controller.Project;
        Packages = controller.ExportCandidates().Select(c => new PackageChoice(c, Replan)).ToList();
        BuildCommand = new RelayCommand(() => _ = BuildAsync(), () => Plan.Ok && !IsBuilding);
        BrowseRetocCommand = new RelayCommand(BrowseRetoc, () => !IsBuilding);
        OpenFolderCommand = new RelayCommand(() => Open(Plan.OutDir), () => Directory.Exists(Plan.OutDir) && !IsBuilding);
        OpenInstructionsCommand = new RelayCommand(() => Open(_instructions!), () => _instructions != null && File.Exists(_instructions));
        _plan = controller.PlanExport(Request);
        SyncImpactWarnings();
    }

    public string Project => _controller.Project;
    public IReadOnlyList<PackageChoice> Packages { get; }
    public bool HasPackages => Packages.Count > 0;

    private string _modName;
    public string ModName { get => _modName; set { if (Set(ref _modName, value ?? "")) Replan(); } }

    private string _container = "pc9999_mobile_and_P";
    /// <summary>Must end in _P: the game mounts patch containers last, so the mod's packages win.</summary>
    public string ContainerName { get => _container; set { if (Set(ref _container, value ?? "")) Replan(); } }

    private string _retocPath;
    public string RetocPath { get => _retocPath; set { if (Set(ref _retocPath, value ?? "")) Replan(); } }

    private ExportRequest Request => new(ModName, ContainerName, Packages.Where(p => p.IsChecked).Select(p => p.PackagePath).ToList(), RetocPath, _acknowledged.ToHashSet());

    private ExportPlan _plan;
    public ExportPlan Plan { get => _plan; private set { if (Set(ref _plan, value)) OnPlanChanged(); } }

    private void Replan() => Plan = _controller.PlanExport(Request);

    // ---- Impact warnings for the picked packages' saved edits (ticks survive re-plans: keyed by the warning) ----

    private readonly HashSet<string> _acknowledged = [];

    private IReadOnlyList<ImpactWarningRow> _impactWarnings = [];
    public IReadOnlyList<ImpactWarningRow> ImpactWarnings { get => _impactWarnings; private set => Set(ref _impactWarnings, value); }
    public bool HasImpactWarnings => ImpactWarnings.Count > 0;
    /// <summary>All of them as one text (one UIA element; lists of equal strings vanish from UIA).</summary>
    public string ImpactWarningsText => ImpactWarnings.Count == 0
        ? "No layout warnings for the picked edits."
        : string.Join("\n", ImpactWarnings.Select(w => w.Text + (w.IsDanger ? w.IsAcknowledged ? " [acknowledged]" : " [tick to acknowledge]" : "")));
    public string ImpactWarningsHeader
    {
        get
        {
            var dangers = ImpactWarnings.Count(w => w.IsDanger);
            return $"Layout warnings (Impact panel): {ImpactWarnings.Count}" + (dangers > 0 ? $", {dangers} danger: tick each to build anyway" : "");
        }
    }

    private void OnAcknowledged(ImpactWarningRow row)
    {
        if (row.IsAcknowledged) _acknowledged.Add(row.Warning.Key);
        else _acknowledged.Remove(row.Warning.Key);
        Replan();
    }

    private void SyncImpactWarnings()
    {
        var warnings = Plan.ImpactWarnings;
        if (warnings.Select(w => w.Key).SequenceEqual(ImpactWarnings.Select(r => r.Warning.Key)))
        {
            OnPropertyChanged(nameof(ImpactWarningsText));
            return; // same warnings: keep the rows (and the focus on a tick box)
        }
        ImpactWarnings = warnings.Select((w, i) => new ImpactWarningRow(w, i, _acknowledged.Contains(w.Key), OnAcknowledged)).ToList();
        OnPropertyChanged(nameof(HasImpactWarnings));
        OnPropertyChanged(nameof(ImpactWarningsText));
        OnPropertyChanged(nameof(ImpactWarningsHeader));
    }

    private void OnPlanChanged()
    {
        SyncImpactWarnings();
        OnPropertyChanged(nameof(RefusalsText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(TexturesText));
        OnPropertyChanged(nameof(OutDirText));
        OnPropertyChanged(nameof(RetocText));
        OnPropertyChanged(nameof(RetocTested));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public string? RefusalsText => Plan.Refusals.Count == 0 ? null : string.Join("\n", Plan.Refusals.Select(r => "• " + r));
    public string? WarningsText => Plan.Warnings.Count == 0 ? null : string.Join("\n", Plan.Warnings.Select(w => "• " + w));
    /// <summary>Step 3: the texture edits the picked packages carry, and the new texture packages they add to the mod.</summary>
    public string? TexturesText => Plan.Textures.Count == 0 ? null : "Textures:\n" + string.Join("\n", Plan.Textures.Select(t =>
        $"• {GamePaths.ShortName(t.Edit.Package)} › {t.Edit.Widget}: {GamePaths.ShortName(t.Edit.Texture)} → {t.Edit.NewName} {t.Width}×{t.Height} " +
        $"(+{t.ClonePaths.Count} new package{(t.ClonePaths.Count == 1 ? "" : "s")}; re-points {string.Join(", ", t.Storing.Select(GamePaths.ShortName))})"));
    public string OutDirText => $"{Plan.OutDir}  (mod\\ = the files for the device; predictions\\ and {ModInstructions.FileName} next to it)";
    public string RetocText => Plan.Retoc.Text;
    public bool RetocTested => Plan.Retoc.IsTested;

    // ---- build ----

    private bool _isBuilding;
    public bool IsBuilding { get => _isBuilding; private set { if (Set(ref _isBuilding, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private string _log = "";
    public string Log { get => _log; private set => Set(ref _log, value); }

    private IReadOnlyList<FileRow> _files = [];
    public IReadOnlyList<FileRow> Files { get => _files; private set => Set(ref _files, value); }

    private string? _result;
    /// <summary>"Built: …" or "Build failed: …" after a build.</summary>
    public string? ResultText { get => _result; private set => Set(ref _result, value); }

    private bool _resultOk;
    public bool ResultOk { get => _resultOk; private set => Set(ref _resultOk, value); }

    private string? _instructions;

    public RelayCommand BuildCommand { get; }
    public RelayCommand BrowseRetocCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenInstructionsCommand { get; }

    private async Task BuildAsync()
    {
        var plan = Plan;
        if (!plan.Ok || IsBuilding) return;
        _saveRetocPath(plan.Retoc.Path);
        IsBuilding = true;
        Files = [];
        ResultText = $"Building {plan.Request.ModName}…";
        ResultOk = false;
        _instructions = null;
        Log = $"retoc path {plan.Retoc.Path} (saved to umgeditor.json)\n{plan.Retoc.Text}\n";
        var progress = new Progress<string>(line => Log += line + "\n");
        try
        {
            var result = await Task.Run(() => _controller.RunExport(plan, new RetocProcess(plan.Retoc.Path), progress));
            await Task.Yield(); // let the last progress lines arrive
            Files = result.Files.Select(f => new FileRow(f)).ToList();
            _instructions = result.InstructionsPath;
            ResultOk = result.Built;
            ResultText = result.Built
                ? $"Built {plan.Request.ModName}: copy the {result.Files.Count} files in {result.ModDir} to the device. {ModInstructions.OnlineWarning}. Test steps: {result.InstructionsPath}"
                : $"Build failed: {string.Join(" | ", result.Refusals)}. Nothing to copy: {result.ModDir} was removed (see FAILED.txt).";
        }
        catch (Exception e)
        {
            ResultText = "Build failed: " + e.Message;
        }
        finally
        {
            IsBuilding = false;
            Replan(); // the folder exists now
        }
    }

    private void BrowseRetoc()
    {
        var dir = Path.GetDirectoryName(RetocPath);
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "retoc|retoc*.exe|Programs|*.exe", InitialDirectory = Directory.Exists(dir) ? dir : null };
        if (dlg.ShowDialog() == true) RetocPath = dlg.FileName;
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
