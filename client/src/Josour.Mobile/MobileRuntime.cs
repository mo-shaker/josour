using System.Net;
using Josour.Core.Control;
using Josour.Core.Security;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Logging;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;
using Josour.Mobile.Platform;
using Josour.Mobile.Services;
using Josour.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Josour.Mobile;

/// <summary>
/// The phone's composition root. One per process, not per activity: Android destroys and recreates the activity (rotation,
/// the user swiping the app away) while the foreground service keeps the process, the control channel and a live session
/// running, so everything here outlives any one screen.
/// </summary>
public static class MobileRuntime
{
    private static readonly object Gate = new();
    private static ServiceProvider? _services;

    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("The phone's runtime has not started.");

    public static bool IsStarted => _services is not null;

    /// <summary>
    /// <c>hello.capabilities</c> for a host-only phone: contact lists and server-held trust, but neither <c>offers</c> (the
    /// phone could not be the guest an offer asks for) nor <c>system_proxy</c>.
    /// </summary>
    public static IReadOnlyList<string> Capabilities { get; } = [ClientCapabilities.Contacts, ClientCapabilities.TrustV2];

    /// <summary>Builds the container once; later calls return the same one.</summary>
    public static IServiceProvider Start(MobilePlatform platform)
    {
        lock (Gate)
        {
            if (_services is not null) return _services;

            Log.Logger = LoggingSetup.CreateConfiguration().CreateLogger();
            var services = new ServiceCollection();
            Configure(services, platform);
            _services = services.BuildServiceProvider();

            var logger = _services.GetRequiredService<ILogger<MobileHostLog>>();
            logger.LogInformation(
                "Josour {AppVersion} starting on {OsVersion} ({OsBuild}) as {DeviceName}; network {Network}",
                platform.Device.AppVersion,
                platform.Device.OsVersion,
                platform.Device.OsBuild,
                platform.Device.DeviceName,
                platform.Network.Current.Kind);

            _services.GetRequiredService<SessionCoordinator>(); // subscribe to the control channel from the start
            _services.GetRequiredService<ConnectivityWatcher>(); // Wi-Fi ↔ mobile data wakes the channel at once
            _ = _services.GetRequiredService<MainViewModel>().StartAsync();
            return _services;
        }
    }

    internal static void Configure(IServiceCollection services, MobilePlatform platform)
    {
        services.AddLogging(b => b.AddSerilog(Log.Logger, dispose: false));
        services.AddSingleton(platform);
        services.AddSingleton(platform.Network);
        services.AddSingleton<IConnectivitySignals>(platform.Network);
        services.AddSingleton(platform.Device);
        services.AddSingleton(platform.Notifier);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IAppSettingsStore>(sp => new AppSettingsStore(sp.GetRequiredService<ILogger<AppSettingsStore>>()));
        services.AddSingleton<ISecretStore>(sp => platform.Secrets(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<HostAvailabilityFlag>();

        services.AddSingleton<IApiClient>(sp =>
        {
            var inner = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), // a move to another network leaves pooled sockets dead
                AutomaticDecompression = DecompressionMethods.All,
            };
            var handler = new AuthenticatedHandler(
                () => sp.GetRequiredService<IAccessTokenSource>(),
                sp.GetRequiredService<ILogger<AuthenticatedHandler>>())
            {
                InnerHandler = inner,
            };
            var http = ApiClient.CreateHttpClient(handler, userAgent: $"{ApiClient.ProductId}/{platform.Device.AppVersion} (Android)");
            return new ApiClient(http, sp.GetRequiredService<IAppSettingsStore>(), sp.GetRequiredService<ILogger<ApiClient>>());
        });
        services.AddSingleton(sp => new AuthSession(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IDeviceInfoProvider>(),
            sp.GetRequiredService<ILogger<AuthSession>>()));
        services.AddSingleton<IAuthSession>(sp => sp.GetRequiredService<AuthSession>());
        services.AddSingleton<IAccessTokenSource>(sp => sp.GetRequiredService<AuthSession>());

        services.AddSingleton<IControlChannel>(sp => new ControlChannel(
            sp.GetRequiredService<IAppSettingsStore>(),
            sp.GetRequiredService<IAccessTokenSource>(),
            sp.GetRequiredService<IDeviceInfoProvider>(),
            ControlChannelOptions.Default with { Capabilities = Capabilities },
            sp.GetRequiredService<ILogger<ControlChannel>>()));
        services.AddSingleton<MobileChannelConnector>();
        services.AddSingleton(sp => new ConnectivityWatcher(
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<IConnectivitySignals>(),
            sp.GetRequiredService<ILogger<ConnectivityWatcher>>(),
            TimeProvider.System,
            ConnectivityWatcherOptions.Default with
            {
                Reconnect = ct => sp.GetRequiredService<MobileChannelConnector>().ConnectAsync(ct),
            }));

        services.AddSingleton<IContactsStore>(sp => new ContactsStore(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<ILogger<ContactsStore>>()));
        services.AddSingleton<IAllowlistDisclosure>(sp => new AllowlistDisclosureService(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ILogger<AllowlistDisclosureService>>()));

        services.AddSingleton<ITunnelSessionFactory, MobileTunnelSessionFactory>();
        services.AddSingleton(sp => new SessionCoordinator(
            sp.GetRequiredService<IControlChannel>(),
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<ITunnelSessionFactory>(),
            HostOnlySystemProxyGuard.Instance,
            HostOnlySystemProxyGuard.Instance,
            sp.GetRequiredService<ILogger<SessionCoordinator>>(),
            TimeProvider.System,
            SessionCoordinatorOptions.Default with { Post = a => Avalonia.Threading.Dispatcher.UIThread.Post(a) }));

        services.AddSingleton<SignInViewModel>();
        services.AddSingleton<HostViewModel>();
        services.AddSingleton<MainViewModel>();
    }

    /// <summary>Ends a live session properly (session.end, not a dropped socket), bounded; for the service being destroyed.</summary>
    public static void EndSessionNow(TimeSpan budget)
    {
        var sessions = _services?.GetService<SessionCoordinator>();
        if (sessions is null || !sessions.HasLiveSession) return;
        try
        {
            sessions.ShutdownAsync(budget).Wait(budget + TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ending the session failed");
        }
    }

    private sealed class MobileHostLog;
}
