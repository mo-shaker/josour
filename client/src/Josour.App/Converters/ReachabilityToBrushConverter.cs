using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Josour.App.Converters;

/// <summary>
/// The reachability dot of a host row: green when the server's TCP probe succeeded (<c>reachable: true</c>),
/// red when it failed, grey while the server has not probed yet (<c>null</c>) — docs/ws-protocol.md section 6.
/// </summary>
public sealed class ReachabilityToBrushConverter : IValueConverter
{
    public static SolidColorBrush ReachableBrush { get; } = Frozen(Color.FromRgb(0x10, 0x7C, 0x10));

    public static SolidColorBrush UnreachableBrush { get; } = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public static SolidColorBrush UnknownBrush { get; } = Frozen(Color.FromRgb(0x8A, 0x88, 0x86));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => ReachableBrush,
        false => UnreachableBrush,
        _ => UnknownBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("ReachabilityToBrushConverter is one-way.");

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
