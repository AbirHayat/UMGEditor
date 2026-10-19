using System.IO;
using System.Windows;
using UmgEditor.App.ViewModels;
using UmgEditor.Core;

namespace UmgEditor.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
        var vm = new MainViewModel(config);
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await vm.LoadAsync();
        };
    }
}
