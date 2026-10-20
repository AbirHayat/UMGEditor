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
    public string Note => candidate.Dirty ? "unsaved changes: save first" : $"{candidate.SavedEdits} saved edit{(candidate.SavedEdits == 1 ? "" : "s")}";

    private bool _checked = !candidate.Dirty;
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) changed(); } }
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
/// The retoc path is a setting: it is saved to umgeditor.json when a build starts.
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

    private ExportRequest Request => new(ModName, ContainerName, Packages.Where(p => p.IsChecked).Select(p => p.PackagePath).ToList(), RetocPath);

    private ExportPlan _plan;
    public ExportPlan Plan { get => _plan; private set { if (Set(ref _plan, value)) OnPlanChanged(); } }

    private void Replan() => Plan = _controller.PlanExport(Request);

    private void OnPlanChanged()
    {
        OnPropertyChanged(nameof(RefusalsText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(OutDirText));
        OnPropertyChanged(nameof(RetocText));
        OnPropertyChanged(nameof(RetocTested));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public string? RefusalsText => Plan.Refusals.Count == 0 ? null : string.Join("\n", Plan.Refusals.Select(r => "• " + r));
    public string? WarningsText => Plan.Warnings.Count == 0 ? null : string.Join("\n", Plan.Warnings.Select(w => "• " + w));
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
