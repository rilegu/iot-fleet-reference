using Microsoft.UI.Xaml;

namespace FleetWinUI;

/// <summary>
/// Boolean to Visibility, which WPF has in the box as BooleanToVisibilityConverter and WinUI
/// does not.
/// </summary>
/// <remarks>
/// A static function rather than an IValueConverter, because x:Bind resolves converters
/// through the XAML root's resource scope and this client's root is a Window — which, unlike
/// a Page, is not a FrameworkElement and so has no resources to resolve against. Function
/// bindings need no scope, and re-evaluate when their argument changes just as a converter
/// would.
/// </remarks>
public static class Vis
{
    public static Visibility When(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
