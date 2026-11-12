using Microsoft.UI.Xaml;

namespace Issun.Views;

/// <summary>
/// Functions for x:Bind, in place of the WPF build's value converters:
/// <c>Visibility="{x:Bind v:Bind.Present(ViewModel.KeyProblem)}"</c>.
/// x:Bind calls them again whenever the property they're given changes.
/// </summary>
public static class Bind
{
    /// <summary>Visible when true.</summary>
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Collapsed when true: the inverse of <see cref="Visible"/>.</summary>
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Visible when the value is a non-empty string or any other non-null object.</summary>
    public static Visibility Present(object? value) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
}
