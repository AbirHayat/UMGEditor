using System.IO;
using System.Windows;
using System.Windows.Automation;
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
        PreviewCanvas.DragStart = _vm.BeginDrag;
        PreviewCanvas.DragMove = _vm.DragTo;
        PreviewCanvas.DragEnd = _vm.EndDrag;
        PreviewCanvas.Nudge = _vm.Nudge;
        PreviewCanvas.ZoomStep = _vm.Preview.StepZoom;
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
            else box.Tag = nameof(TypedState.Applied); // applied: no cue while the inspector re-reads the field
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = field.DisplayValue;
        }
    }

    // typed text that differs from the applied value: the template's triggers colour the box by Tag, the hint is the
    // tooltip, and UI Automation reads the state as ItemStatus
    private void OnFieldTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FieldInspection field } box) return;
        var state = field.Typed(box.Text);
        box.Tag = state.ToString();
        AutomationProperties.SetItemStatus(box, state.ToString());
        if (field.TypedHint(box.Text) is { } hint) box.ToolTip = hint;
        else box.ClearValue(ToolTipProperty);
    }

    // bytes-to-patch tab: copy one value
    private void OnCopyPatchRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatchRow row })
            _vm.CopyText(row.ToText(), $"{row.Slot}.{row.Field}");
    }

    // impact tab: a click on a package (or its Preview button) shows it in the Preview tab with the edits
    private void OnPreviewImpactPackage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ImpactPackage package }) return;
        e.Handled = true;
        _ = _vm.PreviewImpactAsync(package);
    }

    // TreeView.SelectedItem is read-only, so selection is forwarded here
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as WidgetNodeViewModel;
}
