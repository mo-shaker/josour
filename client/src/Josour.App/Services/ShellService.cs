using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Josour.App.Views;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;

namespace Josour.App.Services;

/// <summary>Shows/hides the single MainWindow and LoginWindow and performs the real application exit. All calls are marshalled to the UI thread.</summary>
public sealed class ShellService : IShellService
{
    /// <summary>How long the session cleanup may take before the app exits anyway.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _services; // windows are resolved lazily to avoid MainWindow -> MainViewModel -> IShellService -> MainWindow cycles
    private readonly ILogger<ShellService> _logger;
    private LoginWindow? _login;
    private FirstRunWindow? _firstRun;
    private SettingsWindow? _settings;
    private AboutWindow? _about;

    public ShellService(IServiceProvider services, ILogger<ShellService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public bool IsExiting { get; private set; }

    public void ShowMainWindow() => OnUiThread(() =>
    {
        if (!_services.GetRequiredService<IAuthSession>().IsSignedIn)
        {
            ShowLoginWindowCore();
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        BringToFront(window);
    });

    public void HideMainWindow() => OnUiThread(() => _services.GetRequiredService<MainWindow>().Hide());

    public void ShowLoginWindow() => OnUiThread(ShowLoginWindowCore);

    public void CloseLoginWindow() => OnUiThread(() =>
    {
        var login = _login;
        _login = null;
        login?.Close();
    });

    public void ShowFirstRunWindow() => OnUiThread(() => _firstRun = ShowSingle(_firstRun));

    public void ShowSettingsWindow() => OnUiThread(() => _settings = ShowSingle(_settings));

    public void ShowAboutWindow() => OnUiThread(() => _about = ShowSingle(_about));

    /// <summary>
    /// Opens a folder with the shell's own handler (<c>UseShellExecute</c>), creating it first: the log folder does not
    /// exist until something has been written, and "the folder is missing" is a worse answer than an empty folder.
    /// </summary>
    public bool TryOpenFolder(string path, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "no path";
                return false;
            }

            Directory.CreateDirectory(path);

            // Each desktop has its own "reveal this folder" command. UseShellExecute on a directory path is a
            // Windows behaviour; on macOS the same act is `open`, and asking Windows' way of macOS silently
            // opens nothing at all.
            using var process = OperatingSystem.IsMacOS()
                ? Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { path }, UseShellExecute = false })
                : Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "The folder {Path} could not be opened", path);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Tray → Exit. A live session is ended first (docs/protocol.md section 7: the work browser goes away, the tunnel sends
    /// GOAWAY, the listener and the UPnP mapping are dropped, the secret is zeroed, <c>session.end</c> is reported) and only
    /// then does the process shut down — the UI thread is never blocked while that happens.
    /// </summary>
    public void Exit() => OnUiThread(() =>
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _logger.LogInformation("Exit requested from the tray menu");
        _ = EndSessionThenShutdownAsync();
    });

    private async Task EndSessionThenShutdownAsync()
    {
        try
        {
            await _services.GetRequiredService<SessionCoordinator>().ShutdownAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ending the session before exit failed");
        }

        OnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        });
    }

    /// <summary>
    /// The sign-in surface, whichever one makes sense: an app that does not yet know its server address is sent through
    /// the guided first run instead of a sign-in window whose address field it cannot fill in. The same rule as
    /// <see cref="StartupPlanner"/>, applied to every later "please sign in" too — the tray's Show window, a rejected
    /// refresh token, a revoked device.
    /// </summary>
    private void ShowLoginWindowCore()
    {
        var settings = _services.GetRequiredService<IAppSettingsStore>();
        if (!HttpServerCheck.Validate(settings.Current.ServerUrl).IsOk)
        {
            _logger.LogInformation("Sign-in requested with no usable server address: showing the guided first run");
            _firstRun = ShowSingle(_firstRun);
            return;
        }

        _login = ShowSingle(_login);
    }

    /// <summary>
    /// One instance of a window at a time: a second request brings the existing one forward instead of stacking copies,
    /// and the field is cleared when it closes so the next request builds a fresh one (with a fresh view model).
    /// </summary>
    private T ShowSingle<T>(T? existing)
        where T : Window
    {
        if (existing is not null)
        {
            BringToFront(existing);
            return existing;
        }

        var window = _services.GetRequiredService<T>();
        window.Closed += (_, _) => Forget(window);
        window.Show();
        BringToFront(window);
        return window;
    }

    private void Forget(Window window)
    {
        if (ReferenceEquals(_login, window))
        {
            _login = null;
        }
        else if (ReferenceEquals(_firstRun, window))
        {
            _firstRun = null;
        }
        else if (ReferenceEquals(_settings, window))
        {
            _settings = null;
        }
        else if (ReferenceEquals(_about, window))
        {
            _about = null;
        }
    }

    private static void BringToFront(Window window)
    {
        window.Activate();
        window.Topmost = true; // brief top-most flip reliably brings the window over other apps
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>The single MainWindow, as the desktop lifetime's main window, so closing it exits nothing by itself.</summary>
    public static void AttachMainWindow(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
        }
    }

    private static void OnUiThread(Action action) => UiThread.Post(action);
}
