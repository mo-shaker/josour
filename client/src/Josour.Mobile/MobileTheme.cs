using Avalonia.Controls;

namespace Josour.Mobile;

/// <summary>
/// The type scale (docs/design/brand-tokens.json, <c>mobile.font</c>) as resources a view reads through <c>DynamicResource</c>:
/// one size per role, the line height for the interface language (Arabic sets taller lines and never letter-spacing), both
/// multiplied by the phone's own font size setting. A view is built again on a language switch, so it applies them once.
/// </summary>
public static class MobileTheme
{
    // role, size, line height in English, line height in Arabic (sp)
    private static readonly (string Role, double Size, double ArabicSize, double En, double Ar)[] Scale =
    {
        ("Display", 28, 28, 36, 46),
        ("Title", 22, 22, 28, 36),
        ("Heading", 18, 18, 24, 30),
        ("Body", 16, 16, 24, 28),
        ("Label", 16, 16, 20, 24),
        ("Caption", 14, 14, 20, 24),
        ("Micro", 12, 13, 16, 20),
        ("Countdown", 44, 44, 52, 56),
    };

    /// <summary>Android's "remove animations" (animator duration scale 0): the motif stays still.</summary>
    public static bool ReduceMotion { get => Josour.Brand.BrandMotion.ReduceMotion; set => Josour.Brand.BrandMotion.ReduceMotion = value; }

    /// <summary>Android's font size setting (1 = default); it scales text (non-linearly, as Android does), never the layout.</summary>
    public static double FontScale { get; private set; } = 1;

    /// <summary>Set by the Android head; returns whether it changed, so a running view can be built again.</summary>
    public static bool SetFontScale(double scale)
    {
        var clamped = Math.Clamp(double.IsFinite(scale) ? scale : 1, 0.85, 2.0);
        if (Math.Abs(clamped - FontScale) < 0.001) return false;
        FontScale = clamped;
        return true;
    }

    public static void ApplyTypography(IResourceDictionary resources, bool arabic, double fontScale)
    {
        foreach (var (role, size, arabicSize, en, ar) in Scale)
        {
            var baseSize = arabic ? arabicSize : size;
            var scaled = Scaled(baseSize, fontScale);
            var ratio = scaled / baseSize;
            resources["Josour.FontSize." + role] = scaled;
            resources["Josour.LineHeight." + role] = Math.Round((arabic ? ar : en) * ratio, 1);
            // Right-to-left lines that wrap are laid out by Avalonia with their trailing space counted, so a full line can
            // run up to a space's width past its box and lose its last glyph. A 0.3 em inset on each side keeps it inside.
            var inset = arabic ? Math.Round(0.3 * scaled, 1) : 0;
            resources["Josour.TextInset." + role] = new Avalonia.Thickness(inset, 0);
        }
    }

    // Android 14's non-linear font scaling at 200% (FontScaleConverterTable): small text doubles, large text grows less,
    // so a heading stays a heading instead of breaking its words. Other settings blend linearly towards it.
    private static readonly double[] FromSp = { 8, 10, 12, 14, 18, 20, 24, 30, 100 };
    private static readonly double[] ToSpAt2 = { 16, 20, 24, 28, 32, 35, 40, 45, 100 };

    /// <summary>The size <paramref name="sp"/> takes at the font scale <paramref name="scale"/> (1 = default).</summary>
    public static double Scaled(double sp, double scale)
    {
        if (scale <= 1) return Math.Round(sp * scale, 1);
        var at2 = Interpolate(sp);
        var t = Math.Min(1, scale - 1); // 1 → as is, 2 → the table
        return Math.Round(sp + (at2 - sp) * t, 1);
    }

    private static double Interpolate(double sp)
    {
        if (sp <= FromSp[0]) return sp * 2;
        for (var i = 1; i < FromSp.Length; i++)
        {
            if (sp <= FromSp[i])
            {
                var f = (sp - FromSp[i - 1]) / (FromSp[i] - FromSp[i - 1]);
                return ToSpAt2[i - 1] + f * (ToSpAt2[i] - ToSpAt2[i - 1]);
            }
        }

        return sp;
    }
}
