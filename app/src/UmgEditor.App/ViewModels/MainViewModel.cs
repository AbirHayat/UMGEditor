using System.IO;
using System.Windows.Threading;
using UmgEditor.Core;

namespace UmgEditor.App.ViewModels;

/// <summary>Feature 1: browse every UI package in the stock containers.</summary>
public sealed class MainViewModel : ObservableObject
{
    public const string AllGroups = "(all groups)";
    public const string AllKinds = "(all kinds)";

    private readonly GameConfig _config;
    private readonly DispatcherTimer _searchDelay;
    private GameSource? _source;
    private IReadOnlyList<UiAssetEntry> _all = [];

    public MainViewModel(GameConfig config)
    {
        _config = config;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
        RebuildCommand = new RelayCommand(() => _ = LoadAsync(rebuild: true), () => !IsBusy);
    }

    public string PaksDir => _config.PaksDir;
    public RelayCommand RebuildCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

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
    public UiAssetEntry? Selected { get => _selected; set => Set(ref _selected, value); }

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
        var total = Visible.Count();
        Status = $"Showing {Items.Count:N0} of {total:N0} UI packages" + (_includeBulkImages ? "" : $" ({_all.Count - total:N0} thumbnails/emblems hidden)");
    }
}
