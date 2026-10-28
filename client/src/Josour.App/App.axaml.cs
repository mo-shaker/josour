using System.Globalization;
using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.App.Services.Notifications;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Core.Control;
using Josour.Core.Diagnostics;
using Josour.Core.Net;
using Josour.Core.Security;
using Josour.Infrastructure;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Control.Mock;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Logging;
using Josour.Infrastructure.Security;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;
using Josour.Infrastructure.SystemProxy;
using Serilog;
using Serilog.Extensions.Logging;

namespace Josour.App;

/// <summary>
/// Composition root: Serilog, Generic Host + DI, tray, toasts, then the start-up flow
/// (<see cref="IAuthFlow.RunStartupAsync"/>: silent sign-in → MainWindow / tray, otherwise LoginWindow).
/// </summary>
public partial class App : Application
{
    private readonly StartupOptions _options;
    private readonly SingleInstanceGuard? _instance;
    private IHost? _host;
    private IAppSettingsStore? _settings;
    private string _languageSource = "default";
    private bool _exited;
    private ISystemProxyGuard? _systemProxy;

    public App()
        : this(StartupOptions.Parse(Array.Empty<string>()), null)
    {
        // The XAML previewer constructs an Application with no arguments; it never reaches Initialize's host build.
    }

    public App(StartupOptions options, SingleInstanceGuard? instance)
    {
        _options = options;
        _instance = instance;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>The DI container (for the rare code-behind that needs a service).</summary>
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The host has not started.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the last window must not end the process: Josour lives in the tray.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, e) => OnExit(e.ApplicationExitCode);

            // Log-off and shut-down (WM_QUERYENDSESSION on Windows, the quit event on macOS — Cmd+Q, the Dock's Quit) and a
            // plain SIGTERM do not go through Tray → Exit. A live session is ended there the normal way (session.end with
            // guest_ended/host_ended, not a dropped socket), and the system proxy is put back, while there is still time.
            desktop.ShutdownRequested += (_, _) =>
            {
                EndSessionNow("shutdown requested");
                RestoreSystemProxyNow("shutdown requested");
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemProxyNow("process exit");

            // Only a real desktop run starts the app. The headless UI tests build this same class, and starting there
            // meant every test run signed in to the developer's real server with the developer's real stored session -
            // dozens of parallel refreshes of one rotating token, which the server rightly treats as theft.
            Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start()
    {
        Log.Logger = LoggingSetup.CreateConfiguration().CreateLogger();

        // Before any HTTP client or WebSocket exists: while a guest session points the system proxy at our own listener,
        // this process's traffic (the API, the control channel, the proxy's direct path) must not follow it (ADR-0015).
        LoopSafeWebProxy.InstallAsDefault();

        // The language decides every string in this method, the failure message box included, so it is resolved before
        // anything else — and before the first FrameworkElement exists, which is what makes the metadata override below
        // legal. That is also why the settings store is built here by hand and then handed to the container.
        var bootstrapLoggers = new SerilogLoggerFactory(Log.Logger, dispose: false);
        _settings = new AppSettingsStore(bootstrapLoggers.CreateLogger<AppSettingsStore>());
        ApplyLanguage();

        try
        {
            _host = BuildHost();
            _host.Start();

            var services = _host.Services;
            var logger = services.GetRequiredService<ILogger<App>>();
            var device = services.GetRequiredService<IDeviceInfoProvider>();
            var settings = services.GetRequiredService<IAppSettingsStore>();

            logger.LogInformation(
                "Interface language: {Language} (from {Source}); flow direction {FlowDirection}",
                LocalizedStrings.Current.Language.ToCode(),
                _languageSource,
                UiFlow.Direction);

            logger.LogInformation(
                "Josour {AppVersion} starting on {OsVersion} (build {OsBuild}) as {DeviceName}; minimized={Minimized} toastActivated={ToastActivated} channel={Channel} server={ServerUrl} logs={LogDirectory}",
                device.AppVersion,
                device.OsVersion,
                device.OsBuild,
                device.DeviceName,
                _options.StartMinimized,
                _options.ToastActivated,
                _options.MockControlChannel ? "mock" : "wss",
                string.IsNullOrEmpty(settings.Current.ServerUrl) ? "(not set)" : settings.Current.ServerUrl,
                LoggingSetup.DefaultLogDirectory);

            logger.LogInformation(
                "Secrets are stored with {SecretStore} (encrypted at rest by the platform: {Encrypted})",
                SecretStores.CurrentKind,
                SecretStores.IsEncryptedAtRest);

            if (_instance is { CanReceiveShowWindow: false })
            {
                logger.LogWarning("The show-window channel could not be created; a second launch will not bring this window to the front");
            }

            services.GetRequiredService<INotifier>().RegisterActivation();
            services.GetRequiredService<TrayService>().Initialize();
            services.GetRequiredService<SessionCoordinator>(); // subscribe to the control channel from the start
            services.GetRequiredService<ConnectivityWatcher>(); // sleep/resume and network changes wake the channel
            services.GetRequiredService<SystemConnectivitySignals>().Start();

            var shell = services.GetRequiredService<IShellService>();
            if (_instance is not null)
            {
                _instance.ShowWindowRequested += shell.ShowMainWindow;
                _instance.StartListening();
            }

            _systemProxy = services.GetRequiredService<ISystemProxyGuard>();
            var flow = services.GetRequiredService<IAuthFlow>();
            _ = RunStartupFlowAsync(_systemProxy, flow, logger);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            FatalErrorWindow.Show(
                Strings.StartupFailedTitle,
                string.Format(UiFlow.Culture, Strings.StartupFailedMessageFormat, ex.Message));
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(1);
            }
        }
    }

    /// <summary>
    /// Picks the interface language — <c>--lang</c> first, then the <c>language</c> setting, then Arabic — and fixes it
    /// for the process: the string packs and the formatting culture (Arabic text, Western digits). Runs before any
    /// window exists, because <see cref="UiFlow"/> reads the direction once and every <c>{x:Static}</c> in the views
    /// is evaluated when its XAML loads. There is no live switching; the plan does not ask for one.
    /// </summary>
    private void ApplyLanguage()
    {
        UiLanguage language;
        if (_options.Language is { } fromSwitch)
        {
            language = fromSwitch;
            _languageSource = StartupOptions.LanguageSwitch;
        }
        else if (UiLanguages.TryParse(_settings?.Current.Language, out var fromSettings))
        {
            language = fromSettings;
            _languageSource = "settings.json";
        }
        else
        {
            language = UiLanguages.Default;
            _languageSource = "default";
        }

        var pack = LocalizedStrings.UseLanguage(language);
        CultureInfo.DefaultThreadCurrentCulture = pack.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = pack.Culture;
        Thread.CurrentThread.CurrentCulture = pack.Culture;
        Thread.CurrentThread.CurrentUICulture = pack.Culture;
    }

    private static async Task RunStartupFlowAsync(ISystemProxyGuard systemProxy, IAuthFlow flow, ILogger<App> logger)
    {
        RetireLocalTrustRules(logger);
        RetireWorkBrowser(logger);

        try
        {
            // Before anything can start a session: a system proxy that a previous, unclean run left pointing at a listener
            // that no longer exists means no browsing at all on this machine. Put it back first (ADR-0015).
            if (systemProxy.IsApplied && await systemProxy.RestoreAsync(CancellationToken.None))
            {
                logger.LogWarning("Put back the system proxy settings an earlier session left behind");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The start-up restore of the system proxy failed");
        }

        try
        {
            await flow.RunStartupAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Start-up flow failed");
        }
    }

    /// <summary>
    /// Trusted guests used to live in a local file (ADR-0012). They are held per contact on the server now (ADR-0014), so
    /// the file is removed rather than left to look as though it still meant something. The trust is granted again from
    /// the Contacts tab.
    /// </summary>
    private static void RetireLocalTrustRules(ILogger<App> logger)
    {
        try
        {
            if (File.Exists(AppPaths.AutoAcceptFile))
            {
                File.Delete(AppPaths.AutoAcceptFile);
                logger.LogInformation("Removed the old local trusted-guest file; trust is now set per contact");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove the old trusted-guest file: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The 1.x work browser (ADR-0010) is gone (ADR-0015): its crash marker and its separate browser profile are removed
    /// once, so they do not sit in the data folder looking as though they still meant something.
    /// </summary>
    private static void RetireWorkBrowser(ILogger<App> logger)
    {
        try
        {
            if (File.Exists(AppPaths.LegacyWorkBrowserMarkerFile)) File.Delete(AppPaths.LegacyWorkBrowserMarkerFile);
            if (Directory.Exists(AppPaths.LegacyBrowserProfileDirectory))
            {
                Directory.Delete(AppPaths.LegacyBrowserProfileDirectory, recursive: true);
                logger.LogInformation("Removed the old work-browser profile; any browser now goes through the host");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove the old work-browser files: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// <see cref="SessionCoordinator.ShutdownAsync"/>, synchronous and bounded, for the shutdown paths that do not pass
    /// through Tray → Exit. Waiting here on the UI thread cannot deadlock: the ending runs on the thread pool
    /// (<c>Task.Run</c>, <c>ConfigureAwait(false)</c> throughout) and only ever posts to this thread, never waits on it.
    /// Nothing is cancelled, so an operating-system log-off is never held up beyond the bound.
    /// </summary>
    private void EndSessionNow(string why)
    {
        var sessions = _host?.Services.GetService<SessionCoordinator>();
        if (sessions is null || !sessions.HasLiveSession) return;
        try
        {
            Log.Information("Ending the live session before the process goes ({Why})", why);
            sessions.ShutdownAsync(SessionShutdownBudget).Wait(SessionShutdownBudget + TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ending the session failed ({Why})", why);
        }
    }

    /// <summary>What a shutdown the operating system started may spend on ending a session properly.</summary>
    private static readonly TimeSpan SessionShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>Synchronous and bounded: it runs where the process may be about to go away.</summary>
    private void RestoreSystemProxyNow(string why)
    {
        var guard = _systemProxy;
        if (guard is null || !guard.IsApplied) return;
        try
        {
            Log.Warning("Putting the system proxy back ({Why})", why);
            guard.RestoreAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Putting the system proxy back failed ({Why})", why);
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // The stable identifier, not the display name: IHostEnvironment.ApplicationName feeds
            // configuration and log enrichment, so it should not change with the interface language.
            ApplicationName = ApiClient.ProductId,
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
        if (_instance is not null)
        {
            services.AddSingleton(_instance);
        }


        // Infrastructure: settings, secrets, device. The settings store is the instance OnStartup already read the
        // language from, so the file is parsed once.
        services.AddSingleton<IAppSettingsStore>(_settings ?? new AppSettingsStore());
        services.AddSingleton<ISecretStore>(sp => SecretStores.ForCurrentPlatform(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IDeviceInfoProvider>(_ => new DeviceInfoProvider(typeof(App).Assembly));

        // Host diagnostics, week 6: neither detection is written here any more. The firewall check is Core's single owner
        // (it used to exist in this layer AND in track B's tunnel diagnostics), and the VPN verdict is track B's own
        // HostDiagnostics.DetectVpn() reached through the Core interface, because Infrastructure cannot reference Tunnel.
        services.AddSingleton<IFirewallDiagnostics>(_ => FirewallDiagnostics.ForCurrentPlatform());
        services.AddSingleton<IVpnDetector, TunnelVpnDetector>();
        services.AddSingleton(sp => new HostDiagnosticsProbe(
            sp.GetRequiredService<IFirewallDiagnostics>(),
            sp.GetRequiredService<IVpnDetector>(),
            sp.GetRequiredService<ILogger<HostDiagnosticsProbe>>()));
        services.AddSingleton<IHostReadinessProbe>(sp => sp.GetRequiredService<HostDiagnosticsProbe>());
        services.AddSingleton<HostReadinessMonitor>();

        // The server address the user types: validated like the API client will, then asked GET /healthz on a short budget.
        services.AddSingleton<IServerCheck>(sp => new HttpServerCheck(logger: sp.GetRequiredService<ILogger<HttpServerCheck>>()));

        // REST + auth. AuthenticatedHandler resolves the token source lazily because AuthSession itself calls the API through it.
        services.AddSingleton<IApiClient>(sp =>
        {
            var inner = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = true, // corporate system proxy (plan 8.5)
            };
            var handler = new AuthenticatedHandler(
                () => sp.GetRequiredService<IAccessTokenSource>(),
                sp.GetRequiredService<ILogger<AuthenticatedHandler>>())
            {
                InnerHandler = inner,
            };
            var device = sp.GetRequiredService<IDeviceInfoProvider>();
            var http = ApiClient.CreateHttpClient(handler, userAgent: $"{ApiClient.ProductId}/{device.AppVersion}");
            return new ApiClient(http, sp.GetRequiredService<IAppSettingsStore>(), sp.GetRequiredService<ILogger<ApiClient>>());
        });
        services.AddSingleton<AuthSession>(sp => new AuthSession(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IDeviceInfoProvider>(),
            sp.GetRequiredService<ILogger<AuthSession>>()));
        services.AddSingleton<IAuthSession>(sp => sp.GetRequiredService<AuthSession>());
        services.AddSingleton<IAccessTokenSource>(sp => sp.GetRequiredService<AuthSession>());

        // Control channel: the real WSS channel, or the built-in simulated server with --mock (demos, UI work without a backend).
        // The real channel starts Disconnected — exactly what NotConnectedControlChannel stands for — and ControlChannelConnector
        // only opens it once a server URL is configured AND the user is signed in.
        services.AddSingleton<IControlChannel>(sp => _options.MockControlChannel
            ? new MockControlChannel(
                MockControlChannelOptions.Default,
                TimeProvider.System,
                sp.GetRequiredService<ILogger<MockControlChannel>>())
            : new ControlChannel(
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<IAccessTokenSource>(),
                sp.GetRequiredService<IDeviceInfoProvider>(),
                ControlChannelOptions.Default,
                sp.GetRequiredService<ILogger<ControlChannel>>()));
        services.AddSingleton<ControlChannelConnector>();

        // Contacts (ADR-0014): the lists over REST, kept current by the control channel.
        services.AddSingleton<IContactsStore>(sp => new ContactsStore(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<ILogger<ContactsStore>>()));

        // Sleep/resume and network changes (plan 8.5): the Windows event source, and the watcher that turns a signal into
        // "look at the connection now" instead of waiting out the backoff.
        services.AddSingleton<SystemConnectivitySignals>();
        services.AddSingleton<IConnectivitySignals>(sp => sp.GetRequiredService<SystemConnectivitySignals>());
        services.AddSingleton(sp => new ConnectivityWatcher(
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<IConnectivitySignals>(),
            sp.GetRequiredService<ILogger<ConnectivityWatcher>>(),
            TimeProvider.System,
            ConnectivityWatcherOptions.Default with
            {
                // Nothing running to wake: open the channel again (a no-op unless signed in with a server address).
                Reconnect = ct => sp.GetRequiredService<ControlChannelConnector>().ConnectAsync(ct),
            }));

        // The host's pre-accept disclosure: GET /domains?version=N for the version the request names (product doc 15).
        services.AddSingleton<IAllowlistDisclosure>(sp => new AllowlistDisclosureService(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ILogger<AllowlistDisclosureService>>()));

        // The guest's system proxy (ADR-0015): this platform's controller, the marker written before any change, and the
        // guardian process + restore at login that put the settings back when this process cannot.
        services.AddSingleton<ISystemProxyController>(sp => SystemProxyControllers.ForCurrentPlatform(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ISystemProxyMarkerStore>(sp => new SystemProxyMarkerStore(sp.GetRequiredService<ILogger<SystemProxyMarkerStore>>()));
        services.AddSingleton<ISystemProxySafetyNet>(sp => new SystemProxySafetyNet(sp.GetRequiredService<ILogger<SystemProxySafetyNet>>()));
        services.AddSingleton<ISystemProxyGuard>(sp => new SystemProxyGuard(
            sp.GetRequiredService<ISystemProxyController>(),
            sp.GetRequiredService<ISystemProxyMarkerStore>(),
            sp.GetRequiredService<ISystemProxySafetyNet>(),
            sp.GetRequiredService<ILogger<SystemProxyGuard>>()));
        services.AddSingleton<ICheckPageOpener>(sp => new CheckPageOpener(
            sp.GetRequiredService<IAppSettingsStore>(),
            () => sp.GetRequiredService<IShellService>()));

        // The session: the real tunnel (Track B) behind an injectable factory — the only place the App references Tunnel/Proxy.
        services.AddSingleton<ITunnelSessionFactory, TunnelSessionFactory>();
        services.AddSingleton(sp => new SessionCoordinator(
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ITunnelSessionFactory>(),
            sp.GetRequiredService<ISystemProxyGuard>(),
            sp.GetRequiredService<ICheckPageOpener>(),
            sp.GetRequiredService<ILogger<SessionCoordinator>>(),
            TimeProvider.System,
            SessionCoordinatorOptions.Default with { Post = UiThread.Post }));

        // App services
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IAttentionSound, AttentionSound>();
        services.AddSingleton<IAuthFlow, AuthFlow>();
        services.AddSingleton<IStartupRegistration, StartupRegistration>();
        services.AddSingleton<IToastActivationHandler, ToastActivationHandler>();
        services.AddSingleton<INotifier>(sp => NotifierFactory.Create(
            sp.GetRequiredService<IToastActivationHandler>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IIncomingRequestPresenter, IncomingRequestPresenter>();
        services.AddSingleton<TrayService>();

        // ViewModels + windows (IncomingRequestViewModel/Window are created per request by IncomingRequestPresenter;
        // LoginWindow/LoginViewModel per sign-in by ShellService)
        // Registered rather than left to the constructor's default: HostViewModel is resolved by convention, and an
        // unresolvable optional parameter is a failure mode worth not depending on.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HostViewModel>();
        services.AddSingleton<ContactsViewModel>();
        services.AddSingleton<SessionPanelViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();

        // Week 6 windows. Transient, so each one opens on the settings as they are now rather than as they were at start-up.
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<FirstRunWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<AboutWindow>();
        services.AddTransient<AdminViewModel>();
        services.AddTransient<AdminWindow>();
    }

    private void OnExit(int exitCode)
    {
        if (_exited)
        {
            return;
        }

        _exited = true;
        try
        {
            if (_host is not null)
            {
                _host.Services.GetService<TrayService>()?.Dispose();
                _host.Services.GetService<SystemConnectivitySignals>()?.Dispose(); // SystemEvents holds a strong reference
                _host.Services.GetService<ConnectivityWatcher>()?.Dispose();
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                RestoreSystemProxyNow("exit");

                // DisposeAsync, not Dispose: the control channel is IAsyncDisposable, and a container
                // torn down synchronously refuses those.
                if (_host is IAsyncDisposable asyncHost)
                {
                    asyncHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    _host.Dispose();
                }

                _host = null;
            }

            Log.Information("Josour exited with code {ExitCode}", exitCode);
        }
        finally
        {
            Log.CloseAndFlush();
        }
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
