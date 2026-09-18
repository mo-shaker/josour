using System.Globalization;
using Avalonia.Data.Converters;
using Josour.App.Controls;

namespace Josour.App.Converters;

/// <summary>
/// <c>true</c> → <see cref="InfoBarSeverity.Error"/>, <c>false</c> → <see cref="InfoBarSeverity.Success"/>. One bar can
/// then carry the outcome of a check in both directions instead of two bars taking turns being collapsed.
/// </summary>
public sealed class BooleanToInfoBarSeverityConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo culture) =>
        value is true ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A readiness finding to a severity: <c>true</c> (something to warn about) → <see cref="InfoBarSeverity.Warning"/>,
/// otherwise <see cref="InfoBarSeverity.Success"/>. Used by the first run's readiness summary, where "nothing to report"
/// is itself worth showing — a blank panel would read as "not checked".
/// </summary>
public sealed class WarningFlagToSeverityConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo culture) =>
        value is true ? InfoBarSeverity.Warning : InfoBarSeverity.Success;

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
