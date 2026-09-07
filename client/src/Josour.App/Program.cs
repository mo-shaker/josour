using Microsoft.Toolkit.Uwp.Notifications;
using Josour.App.Services;

namespace Josour.App;

/// <summary>
/// Custom entry point (the csproj sets <c>StartupObject</c>) so single-instance handling runs before WPF spins up.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);

        if (options.UninstallNotifications)
        {
            // Installer's [UninstallRun]: no window, no single-instance mutex, no logging.
            return UninstallNotifications();
        }

        using var instance = SingleInstanceGuard.TryAcquire();
        if (instance is null)
        {
            // Another Josour is already running for this machine: ask it to show its window and leave quietly.
            SingleInstanceGuard.SignalShowWindow();
            return 0;
        }

        var app = new App(options, instance);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Removes what <see cref="ToastNotificationManagerCompat"/> registered under <c>HKCU\Software\Classes</c> (AppUserModelId + COM
    /// activator CLSID). Exit code 0 on success (or off Windows, where there is nothing to remove); 1 when Windows refused.
    /// </summary>
    private static int UninstallNotifications()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            ToastNotificationManagerCompat.Uninstall();
            return 0;
        }
        catch (Exception)
        {
            // Nothing sensible to show from an uninstaller context; the registry keys can be removed by hand if needed.
            return 1;
        }
    }
}
