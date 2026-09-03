using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.App.ViewModels;
using RouteBridge.Core.Control;
using RouteBridge.Core.Security;
using RouteBridge.Infrastructure.Device;
using RouteBridge.Infrastructure.Logging;
using RouteBridge.Infrastructure.Security;
using Serilog;

namespace RouteBridge.App;

/// <summary>Composition root: Serilog, Generic Host + DI, tray, toasts, and the main window.</summary>
public partial class App : Application
{
    private readonly StartupOptions _options;
    private readonly SingleInstanceGuard _instance;
    private IHost? _host;

    public App(StartupOptions options, SingleInstanceGuard instance)
    {
        _options = options;
        _instance = instance;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>The DI container (for the rare code-behind that needs a service).</summary>
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The host has not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = LoggingSetup.CreateConfiguration().CreateLogger();

        try
        {
            _host = BuildHost();
            _host.Start();

            var services = _host.Services;
            var logger = services.GetRequiredService<ILogger<App>>();
            var device = services.GetRequiredService<IDeviceInfoProvider>();

            logger.LogInformation(
                "RouteBridge {AppVersion} starting on {OsVersion} (build {OsBuild}) as {DeviceName}; minimized={Minimized} toastActivated={ToastActivated} logs={LogDirectory}",
                device.AppVersion,
                device.OsVersion,
                device.OsBuild,
                device.DeviceName,
                _options.StartMinimized,
                _options.ToastActivated,
                LoggingSetup.DefaultLogDirectory);

            if (!DpapiSecretStore.IsProtectionAvailable)
            {
                logger.LogWarning("Secret store is using the plaintext development fallback (non-Windows host)");
            }

            if (!_instance.CanReceiveShowWindow)
            {
                logger.LogWarning("Show-window event could not be created; a second launch will not bring this window to the front");
            }

            services.GetRequiredService<ToastService>().RegisterActivation();
            services.GetRequiredService<TrayService>().Initialize();

            var shell = services.GetRequiredService<IShellService>();
            _instance.ShowWindowRequested += shell.ShowMainWindow;
            _instance.StartListening();

            if (_options.StartMinimized)
            {
                logger.LogInformation("Started minimized to the tray");
            }
            else
            {
                shell.ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.StartupFailedMessageFormat, ex.Message),
                Strings.StartupFailedTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = Strings.AppName,
            ContentRootPath = AppContext.BaseDirectory, // the Run-key launch has an arbitrary working directory
        });

        builder.Services.AddSerilog(Log.Logger, dispose: false);
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        ConfigureServices(builder.Services);
        return builder.Build();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_options);
        services.AddSingleton(_instance);

        // Infrastructure
        services.AddSingleton<ISecretStore>(sp => new DpapiSecretStore(sp.GetRequiredService<ILogger<DpapiSecretStore>>()));
        services.AddSingleton<IDeviceInfoProvider>(_ => new DeviceInfoProvider(typeof(App).Assembly));

        // WEEK 2: MockControlChannel (docs/ws-protocol.md); WEEK 3: RouteBridge.Infrastructure.ControlChannel.
        services.AddSingleton<IControlChannel, NotConnectedControlChannel>();

        // App services
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IStartupRegistration, StartupRegistration>();
        services.AddSingleton<IToastActivationHandler, ToastActivationHandler>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
        services.AddSingleton<IIncomingRequestPresenter, IncomingRequestPresenter>();
        services.AddSingleton<TrayService>();

        // ViewModels + windows (IncomingRequestViewModel/Window are created per request by IncomingRequestPresenter)
        services.AddSingleton<HostViewModel>();
        services.AddSingleton<GuestViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.Services.GetService<TrayService>()?.Dispose();
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
                _host = null;
            }

            Log.Information("RouteBridge exited with code {ExitCode}", e.ApplicationExitCode);
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray app alive on UI-thread faults; the log has the details.
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})", e.IsTerminating);
        Log.CloseAndFlush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
