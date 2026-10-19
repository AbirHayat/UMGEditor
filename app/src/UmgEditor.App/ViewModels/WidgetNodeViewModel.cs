using System.Collections.ObjectModel;
using UmgEditor.Core;

namespace UmgEditor.App.ViewModels;

/// <summary>A tree row. Instances of other widget blueprints get their content loaded on first expand, read-only.</summary>
public sealed class WidgetNodeViewModel : ObservableObject
{
    private readonly Func<string, Task<WidgetPackageTree>> _loadEmbedded;
    private bool _embeddedLoaded;

    public WidgetNodeViewModel(WidgetNode node, Func<string, Task<WidgetPackageTree>> loadEmbedded, string? embeddedFrom = null)
    {
        Node = node;
        _loadEmbedded = loadEmbedded;
        EmbeddedFrom = embeddedFrom;
        foreach (var child in node.Children) Children.Add(new WidgetNodeViewModel(child, loadEmbedded, embeddedFrom));
        if (node.EmbeddedPackagePath != null) Children.Add(Placeholder("expand to load contents (read-only)"));
    }

    private WidgetNodeViewModel(string message)
    {
        Node = null;
        _loadEmbedded = _ => Task.FromException<WidgetPackageTree>(new InvalidOperationException());
        Message = message;
    }

    private static WidgetNodeViewModel Placeholder(string message) => new(message);

    public WidgetNode? Node { get; }
    /// <summary>Text-only row (loading / empty / error).</summary>
    public string? Message { get; private set; }
    /// <summary>Short name of the embedded package this row's content comes from; null for the opened package.</summary>
    public string? EmbeddedFrom { get; }

    public ObservableCollection<WidgetNodeViewModel> Children { get; } = [];

    /// <summary>Also the UI Automation name of the tree row.</summary>
    public override string ToString() => Name;

    public string Name => Node?.Name ?? Message ?? "";
    public string ClassName => Node == null ? "" : Node.EmbeddedPackagePath != null ? GamePaths.ShortName(Node.EmbeddedPackagePath) + " (widget)" : Node.ClassName;
    public bool IsReadOnly => Node?.IsReadOnly ?? true;
    public bool IsMessage => Node == null;
    public bool IsAnimated => Node?.IsAnimated == true;
    public bool IsNotAnimated => Node is { IsAnimated: false };
    public bool HasSlot => Node?.Slot != null;
    /// <summary>Animated by a margin (slot) or 2D-transform track: the runtime moves it.</summary>
    public bool IsMovedByAnimation => Node?.Animations.Any(a => a.MovesWidget) == true;
    public bool ClipsToBounds => Node?.ClipsToBounds == true;
    public bool IsEmbeddedInstance => Node?.EmbeddedPackagePath != null;

    public string AnimationTooltip => Node == null ? "" :
        string.Join("\n", Node.Animations.Select(a => $"{a.Animation}{(a.OnSlot ? " [slot]" : "")}: {string.Join(", ", a.Tracks)}"));

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _embeddedLoaded || Node?.EmbeddedPackagePath == null) return;
            _embeddedLoaded = true;
            _ = LoadEmbeddedAsync(Node.EmbeddedPackagePath);
        }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private async Task LoadEmbeddedAsync(string packagePath)
    {
        var placeholder = Children[^1];
        placeholder.Message = "loading…";
        placeholder.OnPropertyChanged(nameof(Name));
        try
        {
            var tree = await _loadEmbedded(packagePath);
            Children.Remove(placeholder);
            var from = GamePaths.ShortName(packagePath);
            if (tree.Root != null) Children.Add(new WidgetNodeViewModel(tree.Root, _loadEmbedded, from));
            else Children.Add(Placeholder($"{from}: no layout of its own (empty WidgetTree)"));
        }
        catch (Exception e)
        {
            placeholder.Message = "failed to load: " + e.Message;
            placeholder.OnPropertyChanged(nameof(Name));
        }
    }
}
