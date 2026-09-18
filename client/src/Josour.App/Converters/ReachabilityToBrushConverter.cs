using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Josour.App.Converters;

/// <summary>
/// The reachability dot of a host row: green when the server's TCP probe succeeded (<c>reachable: true</c>),
/// red when it failed, grey while the server has not probed yet (<c>null</c>) — docs/ws-protocol.md section 6.
/// </summary>
public sealed class ReachabilityToBrushConverter : IValueConverter
{
    public static IBrush ReachableBrush { get; } = Shared(Color.FromRgb(0x10, 0x7C, 0x10));

    public static IBrush UnreachableBrush { get; } = Shared(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public static IBrush UnknownBrush { get; } = Shared(Color.FromRgb(0x8A, 0x88, 0x86));

    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => ReachableBrush,
        false => UnreachableBrush,
        _ => UnknownBrush,
    };

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("ReachabilityToBrushConverter is one-way.");

    /// <summary>Immutable, so one instance is shared by every row instead of one brush per host per update.</summary>
    private static IBrush Shared(Color color) => new ImmutableSolidColorBrush(color);
}
