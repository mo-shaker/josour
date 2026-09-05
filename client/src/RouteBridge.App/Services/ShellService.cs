using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Views;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.App.Services;

/// <summary>Shows/hides the single MainWindow and LoginWindow and performs the real application exit. All calls are marshalled to the UI thread.</summary>
public sealed class ShellService : IShellService
{
    /// <summary>How long the session cleanup may take before the app exits anyway.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _services; // windows are resolved lazily to avoid MainWindow -> MainViewModel -> IShellService -> MainWindow cycles
    private readonly ILogger<ShellService> _logger;
    private LoginWindow? _login;

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

        OnUiThread(() => Application.Current?.Shutdown());
    }

    private void ShowLoginWindowCore()
    {
        if (_login is null)
        {
            var window = _services.GetRequiredService<LoginWindow>();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_login, window))
                {
                    _login = null;
                }
            };
            _login = window;
            window.Show();
        }

        BringToFront(_login);
    }

    private static void BringToFront(Window window)
    {
        window.Activate();
        window.Topmost = true; // brief top-most flip reliably brings the window over other apps
        window.Topmost = false;
        window.Focus();
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
