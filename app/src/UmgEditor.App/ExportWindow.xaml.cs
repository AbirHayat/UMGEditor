using System.Windows;
using System.Windows.Controls;
using UmgEditor.App.ViewModels;

namespace UmgEditor.App;

/// <summary>6g: the export dialog (all logic in ExportViewModel / EditorController).</summary>
public partial class ExportWindow : Window
{
    private readonly ExportViewModel _vm;

    public ExportWindow(ExportViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // a build runs in the background: closing waits for it, so a half-written folder isn't left behind silently
        Closing += (_, e) =>
        {
            if (!_vm.IsBuilding) return;
            e.Cancel = true;
            MessageBox.Show(this, "The build is still running; close the dialog when it has finished.", "Export mod", MessageBoxButton.OK, MessageBoxImage.Information);
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLogChanged(object sender, TextChangedEventArgs e) => ((TextBox) sender).ScrollToEnd();
}
