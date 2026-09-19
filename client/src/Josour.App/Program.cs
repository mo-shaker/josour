using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
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
    /// <summary>
    /// No <c>WithInterFont</c>, and that is the point rather than an omission.
    /// <para>
    /// Bundling Inter is what the Avalonia template does, and it makes an app look identical on every platform —
    /// as long as the app is in a Latin script. Inter has no Arabic at all. Josour's interface is Arabic by default,
    /// and with Inter as the default family every heading rendered as empty boxes: at the normal weight Avalonia
    /// fell back to a system font that has Arabic, and at SemiBold the fallback did not, so exactly the bold text
    /// broke and nothing else. Letting the platform pick its own UI font gives Arabic at every weight — San
    /// Francisco on macOS, Segoe UI on Windows — which is also what the WPF build did.
    /// </para>
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        ConfigureApp(AppBuilder.Configure(() => new App(_options, _instance)))
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Everything about the application that is not the windowing backend — fonts above all.
    /// <para>
    /// It is a separate method so the headless tests can build on the very same one. They used to configure an
    /// <see cref="App"/> of their own, which meant every UI test ran against a differently-configured app than
    /// shipped: when <c>WithInterFont()</c> broke every Arabic heading, forty tests passed. Whatever is added
    /// here is now something the tests actually run against.
    /// </para>
    /// <para>
    /// Note what is deliberately NOT here: a bundled font. The Avalonia template calls <c>WithInterFont()</c>, and
    /// Inter has no Arabic — see the remarks on <see cref="BuildAvaloniaApp"/>. The platform's own UI font is what
    /// this interface needs, and leaving the default alone is how it gets one.
    /// </para>
    /// </summary>
    public static AppBuilder ConfigureApp(AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // macOS needs an Arabic fallback named explicitly, and this is not belt-and-braces — without it the
        // interface is unusable in its own default language.
        //
        // Avalonia's default family on macOS is Helvetica, which has no Arabic at all. At the normal weight the
        // per-glyph fallback finds something anyway; at SemiBold it does not, so every heading, tab and bold label
        // renders as empty boxes while the body text beside it is perfect. That reads like a bug in particular
        // strings, which is why it survived a UI port and a full test suite.
        //
        // Geeza Pro is on every Mac and ships Regular and Bold, so the weights the interface actually uses are
        // covered. Windows needs nothing here: Segoe UI has Arabic at every weight.
        return OperatingSystem.IsMacOS()
            ? builder.With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Geeza Pro") },
                },
            })
            : builder;
    }
}
