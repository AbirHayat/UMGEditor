using System.Text;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Editing;

public enum HexCellKind { Outside, Context, Field }

/// <summary>One byte of a context dump: blank outside the ±32-byte window, the field's bytes marked (and whether the patch changes it).</summary>
public sealed record HexCell(string Text, HexCellKind Kind, bool Changes)
{
    public bool IsField => Kind == HexCellKind.Field;
}

/// <summary>A 16-byte-aligned row of the context dump around a patched value.</summary>
public sealed record HexRow(int Offset, IReadOnlyList<HexCell> Cells)
{
    public string OffsetHex => $"0x{Offset:X6}";

    /// <summary>"0x000A60  E5 5F 00 00 [00 00 00 00] 05 …": the field in brackets, cells outside the window blank, columns aligned.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder(OffsetHex).Append(' ');
            for (int i = 0; i < Cells.Count; i++)
            {
                var opens = Cells[i].IsField && (i == 0 || !Cells[i - 1].IsField);
                var closed = i > 0 && Cells[i - 1].IsField && !Cells[i].IsField;
                sb.Append(opens ? '[' : closed ? ']' : ' ').Append(Cells[i].Text);
            }
            if (Cells.Count > 0 && Cells[^1].IsField) sb.Append(']');
            return sb.ToString().TrimEnd();
        }
    }
}

/// <summary>One value of the bytes-to-patch panel: the PatchEntry plus the widget it belongs to and display text.</summary>
public sealed record PatchRow(PatchEntry Entry, string Widget)
{
    public const int RowBytes = 16;

    /// <summary>"Img_Plate (CanvasPanelSlot_1)"</summary>
    public string Slot => Widget == Entry.SlotName ? Widget : $"{Widget} ({Entry.SlotName})";
    public string Field => Entry.Key.Path.Replace("LayoutData.", "");
    public string TypeText => Entry.Type == FieldType.Float ? "float" : Entry.Type.ToString().ToLowerInvariant();
    public string ValueText => $"{SlotEditor.Format(Entry.OldValue)} → {SlotEditor.Format(Entry.NewValue)}";
    public IReadOnlyList<int> Changed => Entry.ChangedOffsets.ToList();
    public string ChangedText => Changed.Count == 0
        ? "no byte changes"
        : $"{Changed.Count} of {Entry.OldBytes.Length} bytes change: {string.Join(", ", Changed.Select(o => $"0x{o:X}"))}";
    public string MatchText =>
        $"±{BytePlanner.Context}-byte stock context found {Entry.ContextMatches} time{(Entry.ContextMatches == 1 ? "" : "s")} in the chunk (must be exactly 1)";

    /// <summary>The ±32-byte stock window as 16-byte rows, with the field's bytes marked.</summary>
    public IReadOnlyList<HexRow> Context
    {
        get
        {
            var e = Entry;
            int from = e.Offset - e.ContextBefore.Length, to = e.Offset + e.OldBytes.Length + e.ContextAfter.Length;
            byte[] window = [.. e.ContextBefore, .. e.OldBytes, .. e.ContextAfter];
            var rows = new List<HexRow>();
            for (int row = from / RowBytes * RowBytes; row < to; row += RowBytes)
            {
                var cells = new List<HexCell>(RowBytes);
                for (int o = row; o < row + RowBytes; o++)
                {
                    if (o < from || o >= to) { cells.Add(new HexCell("  ", HexCellKind.Outside, false)); continue; }
                    var isField = o >= e.Offset && o < e.Offset + e.OldBytes.Length;
                    cells.Add(new HexCell(window[o - from].ToString("X2"), isField ? HexCellKind.Field : HexCellKind.Context,
                        isField && e.OldBytes[o - e.Offset] != e.NewBytes[o - e.Offset]));
                }
                rows.Add(new HexRow(row, cells));
            }
            return rows;
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {Slot} · {Field} · {TypeText}");
        sb.AppendLine($"    offset   {Entry.OffsetHex} in the chunk");
        sb.AppendLine($"    value    {ValueText}");
        sb.AppendLine($"    old hex  {Entry.OldHex}");
        sb.AppendLine($"    new hex  {Entry.NewHex}   ({ChangedText})");
        sb.AppendLine($"    context  {MatchText}; the field is in [ ]");
        foreach (var r in Context) sb.AppendLine("      " + r.Text);
        return sb.ToString();
    }
}

/// <summary>
/// A value that isn't stored, added as a new 29-byte FloatProperty tag (branch experiment, canary C10): where it goes in
/// the stock chunk, the tag's bytes and the ±32-byte stock context around the insertion point.
/// </summary>
public sealed record InsertRow(TagInsertion Insertion, string Widget, double Default)
{
    public string Slot => Widget == Insertion.SlotName ? Widget : $"{Widget} ({Insertion.SlotName})";
    public string Field => Insertion.Path.Replace("LayoutData.", "");
    public string ValueText => $"default {SlotEditor.Format(Default)} → {SlotEditor.Format(Insertion.Value)}";
    public string WhereText => $"{Insertion.InsertAtHex} (stock offset), in front of the {Insertion.Before} tag";
    public const string TagLayoutText = "Name, Type FloatProperty, Size 4, ArrayIndex 0, no property GUID, then the float";
    public string MatchText =>
        $"±{BytePlanner.Context}-byte stock context around the insertion point found {Insertion.ContextMatches} time{(Insertion.ContextMatches == 1 ? "" : "s")} in the chunk (must be exactly 1)";

    /// <summary>The ±32-byte stock window as 16-byte rows; the tag goes in at the "|".</summary>
    public IReadOnlyList<string> Context
    {
        get
        {
            var i = Insertion;
            int from = i.InsertAt - i.ContextBefore.Length, to = i.InsertAt + i.ContextAfter.Length;
            byte[] window = [.. i.ContextBefore, .. i.ContextAfter];
            var rows = new List<string>();
            for (int row = from / PatchRow.RowBytes * PatchRow.RowBytes; row < to; row += PatchRow.RowBytes)
            {
                var sb = new StringBuilder($"0x{row:X6} ");
                for (int o = row; o < row + PatchRow.RowBytes; o++)
                    sb.Append(o == i.InsertAt ? '|' : ' ').Append(o < from || o >= to ? "  " : window[o - from].ToString("X2"));
                rows.Add(sb.ToString().TrimEnd());
            }
            return rows;
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {Slot} · {Field} · new float tag ({TagInserter.FloatTagSize} bytes)");
        sb.AppendLine($"    insert   {WhereText}");
        sb.AppendLine($"    value    {ValueText}");
        sb.AppendLine($"    tag hex  {Insertion.TagHex}   ({TagLayoutText})");
        sb.AppendLine($"    context  {MatchText}; the tag goes in at |");
        foreach (var r in Context) sb.AppendLine("      " + r);
        return sb.ToString();
    }
}

/// <summary>
/// One animation key value write (branch experiment): section or compiled copy, old → new, and its location proof, the
/// smallest stock window around it that occurs exactly once in the chunk (animation keys only; layout values keep ±32).
/// </summary>
public sealed record AnimRow(AnimWrite Write)
{
    public string What => Write.Description;
    public string Copy => Write.Copy;
    public string ValueText => $"{SlotEditor.Format(Write.OldValue)} → {SlotEditor.Format(Write.NewValue)}";
    public string BytesText => $"{Write.OffsetHex}  {Write.OldHex} → {Write.NewHex}";
    public string WindowText => Write.WindowText;

    public string ToText() =>
        $"  {What} · {Copy} copy (export {Write.Export})\n    offset   {Write.OffsetHex}\n    value    {ValueText}\n    bytes    {Write.OldHex} → {Write.NewHex}\n    proof    {WindowText}\n";
}

/// <summary>The bytes to patch for one package of the project, or why its save would be refused.</summary>
public sealed record PackageBytes(string Package, string FileKey, int StockSize, bool Dirty, IReadOnlyList<PatchRow> Rows, IReadOnlyList<PatchRefusal> Refusals)
{
    public IReadOnlyList<InsertRow> Insertions { get; init; } = [];
    /// <summary>The header values the inserted tags change, as one line (null without insertions).</summary>
    public string? HeaderChanges { get; init; }
    /// <summary>Animation key writes (section and compiled copies), by offset.</summary>
    public IReadOnlyList<AnimRow> AnimRows { get; init; } = [];
    public int AnimKeyCount => AnimRows.Select(r => r.Write.Key).Distinct().Count();
    /// <summary>"63 animation keys in 99 writes; location proof = smallest unique stock window (±32…±384)", null without any.</summary>
    public string? AnimText => AnimRows.Count == 0 ? null
        : $"{AnimKeyCount} animation key{(AnimKeyCount == 1 ? "" : "s")} in {AnimRows.Count} write{(AnimRows.Count == 1 ? "" : "s")} (section + compiled copy where one exists); " +
          $"location proof per write = the smallest stock window that occurs exactly once (±{AnimRows.Min(r => r.Write.Window)}…±{AnimRows.Max(r => r.Write.Window)}, limit ±{BytePlanner.MaxAnimWindow}).";

    public int EditedSize => StockSize + TagInserter.FloatTagSize * Insertions.Count;
    public string Name => GamePaths.ShortName(Package);
    public bool Refused => Refusals.Count > 0;
    public string StateText => (Dirty ? "unsaved" : "saved") + (Refused ? " · save would be refused" : "");
    public string Header => Insertions.Count == 0
        ? $"{Name} · {StateText} · {StockSize:N0} bytes, size unchanged"
        : $"{Name} · {StateText} · {StockSize:N0} → {EditedSize:N0} bytes (+{EditedSize - StockSize}: {Insertions.Count} new tag{(Insertions.Count == 1 ? "" : "s")})";
    /// <summary>Shown when the package has no value left to patch (every edit undone back to stock).</summary>
    public string? EmptyText => Rows.Count == 0 && Insertions.Count == 0 && AnimRows.Count == 0 && !Refused ? "No values to patch: saving writes the stock bytes back." : null;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine($"  {FileKey}");
        if (EmptyText != null) sb.AppendLine("  " + EmptyText);
        foreach (var r in Rows) sb.Append(r.ToText());
        foreach (var r in Insertions) sb.Append(r.ToText());
        if (HeaderChanges != null) sb.AppendLine("  " + HeaderChanges);
        if (AnimText != null) sb.AppendLine("  " + AnimText);
        foreach (var r in AnimRows) sb.Append(r.ToText());
        foreach (var r in Refusals) sb.AppendLine($"  REFUSED [{r.Rule}] {r.Message}");
        return sb.ToString();
    }

    /// <summary>"Header values the new tags change: struct Size @0x… 66 → 95, …; export 11 CookedSerialSize 271 → 300; 1,461 later exports' CookedSerialOffset +29."</summary>
    public static string? HeaderChangesOf(BytePlan plan)
    {
        if (!plan.ChangesSize) return null;
        var sizes = plan.Derived.Where(d => d.Kind != DerivedKind.ExportOffset)
            .Select(d => d.Kind == DerivedKind.StructSize ? $"struct Size @0x{d.Offset:X} {d.Old} → {d.New}" : $"export {d.ExportIndex} CookedSerialSize {d.Old} → {d.New}");
        var moved = plan.Derived.Where(d => d.Kind == DerivedKind.ExportOffset).ToList();
        var by = moved.Select(d => d.New - d.Old).Distinct().Order().ToList();
        return $"Header values the new tags change (stock offsets): {string.Join("; ", sizes)}; {moved.Count:N0} later exports' CookedSerialOffset " +
               (by.Count == 1 ? $"+{by[0]}." : $"+{by.FirstOrDefault()}…+{by.LastOrDefault()}.");
    }
}

/// <summary>What the bytes-to-patch panel shows: every package of the project with edits, as planned on its stock copy.</summary>
public sealed record BytesReport(string Project, IReadOnlyList<PackageBytes> Packages)
{
    public int ValueCount => Packages.Sum(p => p.Rows.Count + p.Insertions.Count + p.AnimKeyCount);
    public int InsertionCount => Packages.Sum(p => p.Insertions.Count);
    public int RefusalCount => Packages.Sum(p => p.Refusals.Count);
    public bool IsEmpty => Packages.Count == 0;

    public string Summary => IsEmpty
        ? $"No edits in project {Project}: nothing to patch."
        : $"{ValueCount} value{(ValueCount == 1 ? "" : "s")} in {Packages.Count} package{(Packages.Count == 1 ? "" : "s")} (project {Project})"
          + (RefusalCount > 0 ? $" · {RefusalCount} refusal{(RefusalCount == 1 ? "" : "s")}" : "")
          + (InsertionCount == 0
              ? ". Every value is written in place over the stock bytes; nothing moves and no size changes."
              : $". Stored values are written in place; {(InsertionCount == 1 ? "1 value that isn't stored is added as a new 29-byte tag" : $"{InsertionCount} values that aren't stored are added as new 29-byte tags")}, " +
                $"so {(Packages.Count(p => p.Insertions.Count > 0) == 1 ? "that package grows" : "those packages grow")} and everything after each tag moves.");

    public string ToText() =>
        $"Bytes to patch: {Summary}\n\n" + string.Join("\n", Packages.Select(p => p.ToText()));

    /// <summary>Plans every session with current edits (or unsaved ones) on its stock copy.</summary>
    /// <param name="widgetOf">Widget name for a slot export of a package, null when unknown (the slot name is used)</param>
    public static BytesReport Build(string project, IEnumerable<PatchSession> sessions, Func<string, int, string?> widgetOf)
    {
        var packages = sessions.Where(s => s.Current.Count > 0 || s.IsDirty)
            .OrderBy(s => GamePaths.ShortName(s.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var plan = s.Plan();
                var rows = plan.Entries.Select(e => new PatchRow(e, widgetOf(s.PackagePath, e.Key.SlotExport) ?? e.SlotName)).ToList();
                var inserts = plan.Insertions.Select(i => new InsertRow(i, widgetOf(s.PackagePath, i.ExportIndex) ?? i.SlotName, i.Key is { } k ? s.BaseValue(k) ?? 0 : 0)).ToList();
                return new PackageBytes(s.PackagePath, s.FileKey, plan.StockSize, s.IsDirty, rows, plan.Refusals)
                    { Insertions = inserts, HeaderChanges = PackageBytes.HeaderChangesOf(plan), AnimRows = plan.AnimWrites.Select(w => new AnimRow(w)).ToList() };
            }).ToList();
        return new BytesReport(project, packages);
    }
}
