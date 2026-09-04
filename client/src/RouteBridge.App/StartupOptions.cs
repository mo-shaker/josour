namespace RouteBridge.App;

/// <summary>Command-line switches understood by RouteBridge.exe.</summary>
/// <param name="StartMinimized"><c>--minimized</c>: start hidden in the tray (used by the Run-key entry written by the installer and by StartupRegistration).</param>
/// <param name="DebugMenu"><c>--debug</c> (or any DEBUG build): show the hidden "Debug" tray menu with "Simulate incoming request".</param>
/// <param name="ToastActivated"><c>-ToastActivated</c>: appended by Windows when a toast button launched the process (Microsoft.Toolkit.Uwp.Notifications).</param>
/// <param name="UninstallNotifications"><c>--uninstall-notifications</c>: remove the toast registration (AppUserModelId + COM activator) and exit; run by the uninstaller.</param>
public sealed record StartupOptions(bool StartMinimized, bool DebugMenu, bool ToastActivated, bool UninstallNotifications = false)
{
    public const string MinimizedSwitch = "--minimized";
    public const string DebugSwitch = "--debug";
    public const string ToastActivatedSwitch = "-ToastActivated";
    public const string UninstallNotificationsSwitch = "--uninstall-notifications";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var minimized = false;
        var debug = false;
        var toast = false;
        var uninstallNotifications = false;

        foreach (var arg in args)
        {
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
        }

#if DEBUG
        debug = true;
#endif

        return new StartupOptions(minimized, debug, toast, uninstallNotifications);
    }
}
