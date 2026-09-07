using System.Globalization;

namespace Josour.Infrastructure.Localization;

/// <summary>The languages the interface ships in. Arabic is the product's language and the default.</summary>
public enum UiLanguage
{
    /// <summary>العربية الفصحى المعاصرة، واتجاه القراءة من اليمين إلى اليسار.</summary>
    Arabic,

    /// <summary>English, left to right.</summary>
    English,
}

/// <summary>
/// Everything the app needs to know about a <see cref="UiLanguage"/>: its code (<c>ar</c> / <c>en</c>), its reading direction
/// and the <see cref="CultureInfo"/> the UI formats with.
/// <para>
/// <b>Digits are deliberately Western in both languages.</b> The culture returned by <see cref="CreateCulture"/> is the
/// language's own culture with the <b>invariant</b> <see cref="NumberFormatInfo"/> grafted on, because everything this app
/// formats as a number is technical — an IP address, a port, a byte count, a version, a duration — and those must stay
/// readable as ASCII digits with a dot decimal separator. An Arabic culture would otherwise render <c>1.5 MB</c> as
/// <c>١٫٥</c> (ICU's default numbering system for <c>ar</c> is <c>arab</c>), which no one can paste into a bug report.
/// The WPF side pairs this with <c>NumberSubstitution.Substitution="European"</c> so the renderer does not substitute the
/// digits back at draw time.
/// </para>
/// </summary>
public static class UiLanguages
{
    public const string ArabicCode = "ar";
    public const string EnglishCode = "en";

    /// <summary>The language the app starts in when nothing says otherwise (product document: the users are Arabic-speaking).</summary>
    public const UiLanguage Default = UiLanguage.Arabic;

    /// <summary><c>ar</c> or <c>en</c>: what goes into the settings file, the <c>--lang</c> switch and the log.</summary>
    public static string ToCode(this UiLanguage language) => language switch
    {
        UiLanguage.English => EnglishCode,
        _ => ArabicCode,
    };

    /// <summary>Arabic reads right to left; every window mirrors accordingly.</summary>
    public static bool IsRightToLeft(this UiLanguage language) => language == UiLanguage.Arabic;

    /// <summary>
    /// Accepts <c>ar</c>, <c>en</c>, a full tag such as <c>ar-SA</c> / <c>en-GB</c>, and the English names of the two
    /// languages; whitespace and case are ignored. Anything else (including null and empty) is not a language choice.
    /// </summary>
    public static bool TryParse(string? value, out UiLanguage language)
    {
        language = Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var primary = trimmed.Split('-', '_')[0];

        if (primary.Equals(ArabicCode, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("arabic", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.Arabic;
            return true;
        }

        if (primary.Equals(EnglishCode, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("english", StringComparison.OrdinalIgnoreCase))
        {
            language = UiLanguage.English;
            return true;
        }

        return false;
    }

    /// <summary>The language, or <see cref="Default"/> when <paramref name="value"/> names none.</summary>
    public static UiLanguage ParseOrDefault(string? value) => TryParse(value, out var language) ? language : Default;

    /// <summary>
    /// The culture the UI formats with: the language's own culture (for collation, casing and text shaping) with the
    /// invariant number format, so digits stay Western. See the class remarks.
    /// </summary>
    public static CultureInfo CreateCulture(this UiLanguage language)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(language.ToCode()).Clone();
        culture.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        return CultureInfo.ReadOnly(culture);
    }
}
