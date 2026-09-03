using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RouteBridge.App.Converters;

/// <summary><c>true</c> → Collapsed, <c>false</c>/null → Visible (the opposite of the built-in BooleanToVisibilityConverter).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
