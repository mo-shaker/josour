using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Views;
using RouteBridge.Infrastructure.Api;

namespace RouteBridge.App.Services;

/// <summary>Shows/hides the single MainWindow and LoginWindow and performs the real application exit. All calls are marshalled to the UI thread.</summary>
public sealed class ShellService : IShellService
{
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

    public void Exit() => OnUiThread(() =>
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _logger.LogInformation("Exit requested from the tray menu");
        // WEEK 5: if a session is active, SessionCoordinator.EndSessionAsync + the cleanup order (protocol.md section 7.6) before shutting down.
        Application.Current.Shutdown();
    });

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
