using System.Collections.Immutable;
using System.Globalization;

namespace UmgEditor.Core.Editing;

/// <summary>Identifies one stored slot value: package, slot export and the dotted field path (same naming as SlotField.Path).</summary>
public sealed record FieldKey(string Package, int SlotExport, string Path)
{
    public static FieldKey Of(WidgetNode node, string path) =>
        new(node.PackagePath, node.Slot?.ExportIndex ?? -1, path);
}

/// <summary>One value change produced by an edit.</summary>
public sealed record FieldChange(FieldKey Key, double OldValue, double NewValue);

/// <summary>
/// Immutable set of edited slot values (field → new value, already rounded to float32 because that's what gets written).
/// Stock data is never modified; layout and save read through this overlay.
/// </summary>
public sealed class EditSet
{
    public static readonly EditSet Empty = new(ImmutableDictionary<FieldKey, double>.Empty);

    private readonly ImmutableDictionary<FieldKey, double> _values;

    private EditSet(ImmutableDictionary<FieldKey, double> values) => _values = values;

    public int Count => _values.Count;
    public IEnumerable<KeyValuePair<FieldKey, double>> Values => _values;

    public bool TryGet(FieldKey key, out double value) => _values.TryGetValue(key, out value);

    /// <summary>Same fields with the same values (dirty state compares the current set with the saved one).</summary>
    public bool ContentEquals(EditSet other) =>
        ReferenceEquals(this, other) || Count == other.Count && _values.All(kv => other.TryGet(kv.Key, out var v) && v.Equals(kv.Value));

    /// <summary>The value layout should use: edited if present, otherwise the stored/default value from the file.</summary>
    public double Effective(WidgetNode node, string path)
    {
        if (TryGet(FieldKey.Of(node, path), out var v)) return v;
        var f = node.Slot?.Fields.FirstOrDefault(x => x.Path == path) ?? throw new KeyNotFoundException($"{node.Name} has no slot field {path}");
        return Parse(f.Value);
    }

    /// <summary>Applies changes. A change back to the stock value removes the entry so "no edits" is exactly Empty.</summary>
    public EditSet With(IEnumerable<FieldChange> changes, Func<FieldKey, double?> stockValue)
    {
        var b = _values.ToBuilder();
        foreach (var c in changes)
        {
            if (stockValue(c.Key) is { } stock && stock == c.NewValue) b.Remove(c.Key);
            else b[c.Key] = c.NewValue;
        }
        return new EditSet(b.ToImmutable());
    }

    public static double Parse(string value) => value switch
    {
        "true" => 1,
        "false" => 0,
        _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
    };
}
