using System.Net;
using Josour.Core.Security;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Device;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.Platform;

/// <summary>How the phone reaches the internet right now. Only what the host screen and the egress policy need.</summary>
public enum NetworkKind { None, WiFi, Cellular, Ethernet, Other }

/// <summary>
/// The phone's network as the operating system reports it: the kind of the default network, whether a VPN holds it, and the
/// addresses and on-link networks the egress policy must block (docs/protocol.md section 6 rules 5 and 6).
/// <para>
/// On a desktop those come from <c>NetworkInterface</c>. On Android its gateway list is empty (the routing table is not
/// readable by apps), so the platform supplies them from <c>LinkProperties</c> instead; the tunnel blocks the union of both.
/// </para>
/// </summary>
public sealed record NetworkSnapshot(
    NetworkKind Kind,
    bool IsVpn,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPNetwork> Networks)
{
    public static NetworkSnapshot Offline { get; } = new(NetworkKind.None, false, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());
}

/// <summary>The network facts, and the signals that wake the control channel when the phone moves between Wi-Fi and mobile data.</summary>
public interface INetworkFacts : IConnectivitySignals
{
    NetworkSnapshot Current { get; }

    /// <summary>Raised on an operating-system thread whenever <see cref="Current"/> changed.</summary>
    event Action<NetworkSnapshot>? Changed;
}

/// <summary>
/// What keeps the process (and so the control channel and any live session) running while the app is not on screen: an
/// Android foreground service with its permanent notification.
/// </summary>
public interface IHostingService
{
    /// <summary>Starts it, or updates its notification when it already runs.</summary>
    void Show(string title, string text, bool sessionActive);

    /// <summary>Stops it (idempotent).</summary>
    void Stop();
}

/// <summary>The notifications that are not the foreground service's own.</summary>
public interface IMobileNotifier
{
    /// <summary>A request is waiting: a high-priority notification that opens the app. It never carries an Accept button.</summary>
    void ShowIncomingRequest(Guid requestId, string title, string body);

    void CancelIncomingRequest(Guid requestId);

    void ShowInfo(string title, string body);

    /// <summary>Renames the notification channels (shown in the phone's settings) after the interface language changed.</summary>
    void RefreshChannelNames();
}

/// <summary>The two system permissions a host needs to stay reachable in the background.</summary>
public interface IMobilePermissions
{
    /// <summary>Android 13+ asks before an app may post notifications.</summary>
    bool NeedsNotificationPermission { get; }

    void RequestNotificationPermission();

    /// <summary>Doze closes idle sockets; an app that is "optimized" loses its control channel with the screen off.</summary>
    bool IsBatteryOptimized { get; }

    void RequestIgnoreBatteryOptimizations();

    /// <summary>Raised when the user may have answered one of the above (the app came back to the front).</summary>
    event Action? Changed;
}

/// <summary>Everything the phone's interface needs from the operating system, supplied by Josour.Android.</summary>
public sealed record MobilePlatform(
    IDeviceInfoProvider Device,
    Func<ILoggerFactory, ISecretStore> Secrets,
    INetworkFacts Network,
    IHostingService Hosting,
    IMobileNotifier Notifier,
    IMobilePermissions Permissions);
