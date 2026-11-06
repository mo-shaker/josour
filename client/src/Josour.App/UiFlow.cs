using System.Globalization;
using Avalonia.Media;
using Josour.Infrastructure.Localization;

namespace Josour.App;

/// <summary>
/// The reading direction of the interface, for XAML to bind to with <c>{x:Static app:UiFlow.Direction}</c>.
/// <para>
/// Avalonia mirrors a subtree whose <see cref="FlowDirection"/> is <see cref="FlowDirection.RightToLeft"/> by itself:
/// grid columns, <c>HorizontalAlignment.Left/Right</c>, <c>Margin</c> and <c>Padding</c> are all expressed in reading
/// order and swap sides. So the views keep their layout as written and only the window roots carry the direction.
/// What Avalonia does NOT mirror is a glyph: an arrow drawn pointing right keeps pointing right in a mirrored window,
/// which is why <see cref="GlyphMirror"/> exists for the few directional icons.
/// </para>
/// <para>
/// Values are read from <see cref="LocalizedStrings.Current"/>, which <c>App.Start</c> fixes before any window is created; the
/// language does not change while the app runs. They are read on each use, not cached, so the headless tests can draw every
/// window in both languages in one process.
/// </para>
/// </summary>
public static class UiFlow
{
    private static readonly ITransform Mirror = new ScaleTransform(-1, 1);

    /// <summary>True while the interface is Arabic.</summary>
    public static bool IsRightToLeft => LocalizedStrings.Current.IsRightToLeft;

    /// <summary>The window-level flow direction: right to left in Arabic, left to right in English.</summary>
    public static FlowDirection Direction => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// Always <see cref="FlowDirection.LeftToRight"/>: for the fields that hold a technical value — a server URL, an
    /// email address, an IP, a host name, a version — which must not be mirrored even inside an Arabic window.
    /// </summary>
    public static FlowDirection Technical => FlowDirection.LeftToRight;

    /// <summary>
    /// Where a left-to-right technical value (<see cref="Technical"/>) lines up: at the reading start, so in an Arabic window
    /// it sits under the right-aligned labels instead of jumping to the far edge. A path that starts with "~" or "%" needs the
    /// whole block left to right: an embedding mark alone lets the leading symbol move to the end.
    /// </summary>
    public static TextAlignment TechnicalAlignment => IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;

    /// <summary>
    /// Mirrors a directional glyph (a "send" arrow) in a right-to-left interface, and is null otherwise. Use with
    /// <c>RenderTransformOrigin="50%,50%"</c>. One instance is shared by every icon that needs it.
    /// </summary>
    public static ITransform? GlyphMirror => IsRightToLeft ? Mirror : null;

    /// <summary>The culture every <c>string.Format</c> in the UI uses (Arabic text, Western digits).</summary>
    public static CultureInfo Culture => LocalizedStrings.Current.Culture;

    /// <summary>Wraps a technical value so bidi keeps it left to right inside Arabic prose; see <see cref="LocalizedStrings.Ltr(string?)"/>.</summary>
    public static string Ltr(string? value) => LocalizedStrings.Current.Ltr(value);
}
