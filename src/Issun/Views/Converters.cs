using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Issun.Views;

/// <summary>Collapsed when the value is true — the inverse of BooleanToVisibilityConverter.</summary>
public sealed class CollapsedWhenTrue : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is a non-empty string or any other non-null object.</summary>
public sealed class VisibleWhenPresent : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
