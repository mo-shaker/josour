using Microsoft.Extensions.Logging;

namespace Josour.App.Services.Notifications;

/// <summary>
/// Picks the notifier this machine actually has.
/// <para>
/// The three implementations are not equivalent, and the difference is the reason this seam exists. Windows can show a
/// toast with Accept and Reject on it; macOS can show a banner but — for an app that is neither signed nor bundled —
/// not one with buttons. So the request WINDOW, not the notification, is the path that must always work, on both. The
/// notification is how the app gets attention, never how it takes consent.
/// </para>
/// </summary>
public static class NotifierFactory
{
    public static INotifier Create(IToastActivationHandler handler, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(loggers);

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new WindowsToastNotifier(handler, loggers.CreateLogger<WindowsToastNotifier>());
        }
#endif

        if (OperatingSystem.IsMacOS())
        {
            return new MacNotifier(loggers.CreateLogger<MacNotifier>());
        }

        return new NullNotifier(loggers.CreateLogger<NullNotifier>());
    }

    /// <summary>
    /// The installer's <c>--uninstall-notifications</c>: removes whatever the platform registered. Exit code 0 on
    /// success or where there is nothing to remove, 1 when the platform refused.
    /// </summary>
    public static int Uninstall()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WindowsToastNotifier.UninstallRegistration();
        }
#endif

        // macOS registers nothing: notifications are posted per launch and leave no state behind.
        return 0;
    }
}
