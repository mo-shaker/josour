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
/// Values are read once, on first use, from <see cref="LocalizedStrings.Current"/> — which <c>App.Initialize</c> fixes
/// before any window is created. The language does not change while the app runs.
/// </para>
/// </summary>
public static class UiFlow
{
    private static readonly Lazy<bool> RightToLeftLazy = new(() => LocalizedStrings.Current.IsRightToLeft);
    private static readonly Lazy<ITransform?> GlyphMirrorLazy = new(CreateGlyphMirror);

    /// <summary>True while the interface is Arabic.</summary>
    public static bool IsRightToLeft => RightToLeftLazy.Value;

    /// <summary>The window-level flow direction: right to left in Arabic, left to right in English.</summary>
    public static FlowDirection Direction => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// Always <see cref="FlowDirection.LeftToRight"/>: for the fields that hold a technical value — a server URL, an
    /// email address, an IP, a host name, a version — which must not be mirrored even inside an Arabic window.
    /// </summary>
    public static FlowDirection Technical => FlowDirection.LeftToRight;

    /// <summary>
    /// Mirrors a directional glyph (a "send" arrow) in a right-to-left interface, and is null otherwise. Use with
    /// <c>RenderTransformOrigin="50%,50%"</c>. One instance is shared by every icon that needs it.
    /// </summary>
    public static ITransform? GlyphMirror => GlyphMirrorLazy.Value;

    /// <summary>The culture every <c>string.Format</c> in the UI uses (Arabic text, Western digits).</summary>
    public static CultureInfo Culture => LocalizedStrings.Current.Culture;

    /// <summary>Wraps a technical value so bidi keeps it left to right inside Arabic prose; see <see cref="LocalizedStrings.Ltr(string?)"/>.</summary>
    public static string Ltr(string? value) => LocalizedStrings.Current.Ltr(value);

    private static ITransform? CreateGlyphMirror() =>
        LocalizedStrings.Current.IsRightToLeft ? new ScaleTransform(-1, 1) : null;
}
