using System.IO;
using System.Windows;
using UmgEditor.App.ViewModels;
using UmgEditor.Core;

namespace UmgEditor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
        _vm = new MainViewModel(config);
        DataContext = _vm;
        PreviewCanvas.BoxClicked += box => _vm.SelectFromPreview(box);
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _vm.LoadAsync();
        };
    }

    // TreeView.SelectedItem is read-only, so selection is forwarded here
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as WidgetNodeViewModel;
}
