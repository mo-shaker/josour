using System.Globalization;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Settings;

namespace Josour.Mobile;

/// <summary>
/// The phone's interface language: English unless the user picked Arabic with the switch on the screen, which is saved as
/// the <c>language</c> setting. Unlike the desktop it does not follow the phone's own language and does not need a restart:
/// <see cref="ViewModels.MainViewModel"/> switches and the view is rebuilt.
/// </summary>
public static class MobileLanguage
{
    /// <summary>The language a phone starts in when the settings name none.</summary>
    public const UiLanguage Default = UiLanguage.English;

    /// <summary>The saved choice, or <see cref="Default"/>; an empty or unknown value is not a choice.</summary>
    public static UiLanguage FromSettings(AppSettings settings) =>
        UiLanguages.TryParse(settings.Language, out var language) ? language : Default;

    /// <summary>The other one of the two languages, for the switch.</summary>
    public static UiLanguage Other(UiLanguage language) =>
        language == UiLanguage.English ? UiLanguage.Arabic : UiLanguage.English;

    /// <summary>Makes <paramref name="language"/> the process's strings and formatting culture (Western digits in both).</summary>
    public static void Apply(UiLanguage language)
    {
        var pack = LocalizedStrings.UseLanguage(language);
        CultureInfo.DefaultThreadCurrentCulture = pack.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = pack.Culture;
        CultureInfo.CurrentCulture = pack.Culture;
        CultureInfo.CurrentUICulture = pack.Culture;
    }
}
