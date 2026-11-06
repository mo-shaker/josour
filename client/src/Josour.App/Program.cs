using Avalonia;
using Avalonia.Media;
using Josour.App.Services;
using Josour.Brand;
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

        // The system-proxy helpers (ADR-0015) run beside the app, not instead of it: no window, no single-instance mutex.
        if (_options.ProxyGuardian is { } guardian)
        {
            return SystemProxyCommands.RunGuardian(guardian);
        }

        if (_options.RestoreProxy)
        {
            return SystemProxyCommands.Restore();
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
        ConfigureApp(AppBuilder.Configure(() => new App(_options, _instance)))
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Everything about the application that is not the windowing backend — fonts above all.
    /// <para>
    /// It is a separate method so the headless tests build on the very same one: whatever is added here is something the
    /// tests actually run against.
    /// </para>
    /// <para>
    /// The interface font is Readex Pro, bundled in Josour.Brand (the wordmark's own typeface, Arabic and Latin at every
    /// weight the views use), registered as the fallback for any text drawn outside a styled window. On macOS Geeza Pro
    /// follows it, for a character Readex Pro does not have: Avalonia's default family there is Helvetica, which has no
    /// Arabic, and its per-glyph fallback finds none at SemiBold.
    /// </para>
    /// </summary>
    public static AppBuilder ConfigureApp(AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return OperatingSystem.IsMacOS()
            ? BrandFonts.Use(builder, new FontFamily("Geeza Pro"))
            : BrandFonts.Use(builder);
    }
}
