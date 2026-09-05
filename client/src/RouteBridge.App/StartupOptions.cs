using RouteBridge.Infrastructure.Localization;

namespace RouteBridge.App;

/// <summary>Command-line switches understood by RouteBridge.exe.</summary>
/// <param name="StartMinimized"><c>--minimized</c>: start hidden in the tray (used by the Run-key entry written by the installer and by StartupRegistration).</param>
/// <param name="DebugMenu"><c>--debug</c> (or any DEBUG build): show the hidden "Debug" tray menu with "Simulate incoming request".</param>
/// <param name="ToastActivated"><c>-ToastActivated</c>: appended by Windows when a toast button launched the process (Microsoft.Toolkit.Uwp.Notifications).</param>
/// <param name="UninstallNotifications"><c>--uninstall-notifications</c>: remove the toast registration (AppUserModelId + COM activator) and exit; run by the uninstaller.</param>
/// <param name="MockControlChannel"><c>--mock</c>: talk to the built-in simulated server instead of <c>wss://…/ws</c> (demos and UI work without a backend).</param>
/// <param name="Language"><c>--lang ar|en</c> (also <c>--lang=ar</c>): the interface language for this run only; null means "use the setting, else Arabic".</param>
public sealed record StartupOptions(
    bool StartMinimized,
    bool DebugMenu,
    bool ToastActivated,
    bool UninstallNotifications = false,
    bool MockControlChannel = false,
    UiLanguage? Language = null)
{
    public const string MinimizedSwitch = "--minimized";
    public const string DebugSwitch = "--debug";
    public const string ToastActivatedSwitch = "-ToastActivated";
    public const string UninstallNotificationsSwitch = "--uninstall-notifications";
    public const string MockChannelSwitch = "--mock";
    public const string LanguageSwitch = "--lang";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var minimized = false;
        var debug = false;
        var toast = false;
        var uninstallNotifications = false;
        var mock = false;
        UiLanguage? language = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, MinimizedSwitch, StringComparison.OrdinalIgnoreCase))
            {
                minimized = true;
            }
            else if (string.Equals(arg, DebugSwitch, StringComparison.OrdinalIgnoreCase))
            {
                debug = true;
            }
            else if (string.Equals(arg, ToastActivatedSwitch, StringComparison.OrdinalIgnoreCase))
            {
                toast = true;
            }
            else if (string.Equals(arg, UninstallNotificationsSwitch, StringComparison.OrdinalIgnoreCase))
            {
                uninstallNotifications = true;
            }
            else if (string.Equals(arg, MockChannelSwitch, StringComparison.OrdinalIgnoreCase))
            {
                mock = true;
            }
            else if (arg.StartsWith(LanguageSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                language = ParseLanguage(arg[(LanguageSwitch.Length + 1)..], language);
            }
            else if (string.Equals(arg, LanguageSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                language = ParseLanguage(args[++i], language);
            }
        }

#if DEBUG
        debug = true;
#endif

        return new StartupOptions(minimized, debug, toast, uninstallNotifications, mock, language);
    }

    /// <summary>An unusable <c>--lang</c> value is ignored (the settings file, then Arabic, still decide) rather than fatal.</summary>
    private static UiLanguage? ParseLanguage(string value, UiLanguage? current) =>
        UiLanguages.TryParse(value, out var parsed) ? parsed : current;
}
