using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RouteBridge.App.Services;

/// <summary>Shows/hides the single MainWindow and performs the real application exit. All calls are marshalled to the UI thread.</summary>
public sealed class ShellService : IShellService
{
    private readonly IServiceProvider _services; // MainWindow is resolved lazily to avoid a MainWindow -> MainViewModel -> IShellService -> MainWindow cycle
    private readonly ILogger<ShellService> _logger;

    public ShellService(IServiceProvider services, ILogger<ShellService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public bool IsExiting { get; private set; }

    public void ShowMainWindow() => OnUiThread(() =>
    {
        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true; // brief top-most flip reliably brings the window over other apps
        window.Topmost = false;
        window.Focus();
    });

    public void HideMainWindow() => OnUiThread(() => _services.GetRequiredService<MainWindow>().Hide());

    public void Exit() => OnUiThread(() =>
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _logger.LogInformation("Exit requested from the tray menu");
        // WEEK 5: if a session is active, send session.end via IControlChannel and run the cleanup order (protocol.md section 7) before shutting down.
        Application.Current.Shutdown();
    });

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
