using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Modding;

/// <summary>
/// <c>umgmod build</c>: the export dialog's plan and run (EditorController.PlanExport / RunExport), so the CLI refuses
/// what the dialog refuses and writes the same mod\, predictions\ and test instructions. The dialog's Danger tick boxes
/// are the <c>--ack-danger</c> flag: it acknowledges every Danger warning, which is printed first.
/// </summary>
public static class BuildCommand
{
    public const int Done = 0, Refused = 2, NeedsAck = 3;
    public const string AckFlag = "--ack-danger";

    public static int Run(EditorController controller, ExportRequest request, bool ackDanger, IRetoc retoc, TextWriter output, TextWriter error)
    {
        var plan = controller.PlanExport(request);
        if (ackDanger && plan.Unacknowledged.Count > 0)
            plan = controller.PlanExport(request with { Acknowledged = plan.ImpactWarnings.Where(w => w.IsDanger).Select(w => w.Key).ToList() });

        output.WriteLine(plan.ImpactWarnings.Count == 0 ? "layout warnings: none" : $"layout warnings ({plan.ImpactWarnings.Count}):");
        foreach (var w in plan.ImpactWarnings)
            output.WriteLine($"  {w.Text}{(w.IsDanger ? plan.Request.IsAcknowledged(w) ? $" (acknowledged by {AckFlag})" : " (NOT acknowledged)" : "")}");
        foreach (var w in plan.Warnings) output.WriteLine($"warning: {w}");
        if (!plan.Ok)
        {
            foreach (var r in plan.Refusals) error.WriteLine($"refused: {r}");
            // only the acknowledgment missing: like a first edit without --yes, read the warnings and rerun with the flag
            if (plan.Unacknowledged.Count > 0 && plan.Refusals.Count == 1)
            {
                error.WriteLine($"Read the danger warning{(plan.Unacknowledged.Count == 1 ? "" : "s")} above, then rerun with {AckFlag} to build anyway.");
                return NeedsAck;
            }
            return Refused;
        }

        var result = controller.RunExport(plan, retoc, new LineProgress(output));
        if (!result.Built)
        {
            foreach (var r in result.Refusals) error.WriteLine($"refused: {r}");
            return Refused;
        }
        output.WriteLine($"OK: copy these {result.Files.Count} files from {result.ModDir} next to the stock containers on the device:");
        foreach (var f in result.Files) output.WriteLine($"  {f.Name}  {f.Size} bytes  sha256 {f.Sha256}");
        output.WriteLine($"Test steps: {result.InstructionsPath}");
        return Done;
    }

    /// <summary>Writes each progress line as it comes (the CLI runs the build on its own thread).</summary>
    private sealed class LineProgress(TextWriter output) : IProgress<string>
    {
        public void Report(string value) => output.WriteLine(value);
    }
}
