using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UmgEditor.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Collapsed when the bound value is null or an empty string.</summary>
public sealed class NullToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value == null || value is "" ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound bool is false.</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A warning as a list row. A reference type on purpose: WPF keys item automation peers by Equals, and EditWarning is a
/// record, so a list rebuilt with an equal warning (every drag step, every edit) lost its rows in UI Automation.
/// </summary>
public sealed class WarningView(UmgEditor.Core.Editing.EditWarning warning)
{
    public UmgEditor.Core.Editing.WarningKind Kind => warning.Kind;
    public UmgEditor.Core.Editing.Severity Severity => warning.Severity;
    public string Message => warning.Message;

    public static IReadOnlyList<WarningView> Of(IEnumerable<UmgEditor.Core.Editing.EditWarning> warnings) => warnings.Select(w => new WarningView(w)).ToList();
}

/// <summary>Visible when the bound count is 0.</summary>
public sealed class ZeroToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
