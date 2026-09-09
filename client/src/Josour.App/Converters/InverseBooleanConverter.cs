using System.Globalization;
using System.Windows.Data;

namespace Josour.App.Converters;

/// <summary><c>true</c> ↔ <c>false</c> (for IsOpen/IsEnabled bindings that need the negation).</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
