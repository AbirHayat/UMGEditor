using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UmgEditor.App.ViewModels;
using UmgEditor.Core;
using UmgEditor.Core.Editing;

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
        Closing += (_, e) => e.Cancel = !_vm.ConfirmClose();
        // a cancelled switch: the binding has already re-read SelectedItem (the open package) while the row the user
        // picked is still highlighted, so setting SelectedItem again is a no-op; select by index to move the highlight back
        _vm.SwitchRefused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PackageList.SelectedIndex = PackageList.Items.IndexOf(_vm.Selected);
            ProjectPicker.SelectedIndex = ProjectPicker.Items.IndexOf(_vm.SelectedProject);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    // inspector field: Enter applies the typed value, Esc puts the current value back
    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FieldInspection field } box) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!_vm.CommitField(field, box.Text)) box.Text = field.DisplayValue;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = field.DisplayValue;
        }
    }

    // TreeView.SelectedItem is read-only, so selection is forwarded here
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as WidgetNodeViewModel;
}
