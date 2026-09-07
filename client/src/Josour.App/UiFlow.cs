using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Josour.Infrastructure.Localization;

namespace Josour.App;

/// <summary>
/// The reading direction of the interface, for XAML to bind to with <c>{x:Static app:UiFlow.Direction}</c>.
/// <para>
/// WPF mirrors a subtree whose <see cref="FlowDirection"/> is <see cref="FlowDirection.RightToLeft"/> by itself: grid
/// columns, <c>HorizontalAlignment.Left/Right</c>, <c>Margin</c> and <c>Padding</c> are all expressed in reading order and
/// swap sides. So the views keep their layout as written and only the window roots (and the tray menu, which is its own
/// visual root) carry the direction. What WPF does NOT mirror is a glyph: an arrow drawn pointing right keeps pointing
/// right in a mirrored window, which is why <see cref="GlyphMirror"/> exists for the few directional icons.
/// </para>
/// <para>
/// Values are read once, on first use, from <see cref="LocalizedStrings.Current"/> — which <c>App.OnStartup</c> fixes
/// before any window is created. The language does not change while the app runs.
/// </para>
/// </summary>
public static class UiFlow
{
    private static readonly Lazy<bool> RightToLeftLazy = new(() => LocalizedStrings.Current.IsRightToLeft);
    private static readonly Lazy<Transform> GlyphMirrorLazy = new(CreateGlyphMirror);

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
    /// Mirrors a directional glyph (a "send" arrow) in a right-to-left interface, and leaves it alone otherwise. Use with
    /// <c>RenderTransformOrigin="0.5,0.5"</c>. Frozen, so one instance is shared by every icon that needs it.
    /// </summary>
    public static Transform GlyphMirror => GlyphMirrorLazy.Value;

    /// <summary>The XAML language tag of the interface, so WPF shapes and hyphenates the text as that language.</summary>
    public static XmlLanguage Language => XmlLanguage.GetLanguage(LocalizedStrings.Current.Culture.IetfLanguageTag);

    /// <summary>Right-to-left reading for the one message box the app shows before any window exists.</summary>
    public static MessageBoxOptions MessageBoxDirection =>
        IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    /// <summary>The culture every <c>string.Format</c> in the UI uses (Arabic text, Western digits).</summary>
    public static CultureInfo Culture => LocalizedStrings.Current.Culture;

    /// <summary>Wraps a technical value so bidi keeps it left to right inside Arabic prose; see <see cref="LocalizedStrings.Ltr(string?)"/>.</summary>
    public static string Ltr(string? value) => LocalizedStrings.Current.Ltr(value);

    /// <summary>Applies the interface direction and language to a root that WPF does not reach through inheritance.</summary>
    public static void Apply(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.FlowDirection = Direction;
        element.Language = Language;
    }

    private static Transform CreateGlyphMirror()
    {
        if (!LocalizedStrings.Current.IsRightToLeft)
        {
            return Transform.Identity;
        }

        var mirror = new ScaleTransform(-1, 1);
        mirror.Freeze();
        return mirror;
    }
}
