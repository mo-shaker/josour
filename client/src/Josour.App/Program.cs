using Avalonia;
using Josour.App.Services;
using Josour.App.Services.Notifications;

namespace Josour.App;

/// <summary>
/// Entry point. Single-instance handling runs before Avalonia starts, so a second launch never builds a UI it is
/// only going to throw away.
/// </summary>
public static class Program
{
    private static StartupOptions _options = StartupOptions.Parse(Array.Empty<string>());
    private static SingleInstanceGuard? _instance;

    [STAThread]
    public static int Main(string[] args)
    {
        _options = StartupOptions.Parse(args);

        if (_options.UninstallNotifications)
        {
            // Installer's [UninstallRun]: no window, no single-instance mutex, no logging.
            return NotifierFactory.Uninstall();
        }

        using var instance = SingleInstanceGuard.TryAcquire();
        if (instance is null)
        {
            // Another Josour is already running for this machine: ask it to show its window and leave quietly.
            SingleInstanceGuard.SignalShowWindow();
            return 0;
        }

        _instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// Also the designer's entry point, which is why it is public, parameterless and free of side effects.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(_options, _instance))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
