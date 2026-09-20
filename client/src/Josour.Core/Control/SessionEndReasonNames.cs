using Josour.Core.Tunnel;

namespace Josour.Core.Control;

/// <summary>
/// The end reasons' names on the wire as in docs/ws-protocol.md section 5 (<c>session.end.reason</c> and <c>session.terminate.reason</c>).
/// The table in section 3 mentions <c>guest_ended</c>/<c>host_ended</c> only because that is the usual case, while section 5 enumerates
/// the rest (<c>expired</c>, <c>browser_not_proxied</c>, …) and the client needs to report them literally. The name here is section 5's spelling.
/// </summary>
public static class SessionEndReasonNames
{
    public const string GuestEnded = "guest_ended";
    public const string HostEnded = "host_ended";
    public const string Expired = "expired";
    public const string GuestDisconnected = "guest_disconnected";
    public const string HostDisconnected = "host_disconnected";
    public const string ConnectFailed = "connect_failed";
    public const string AdminTerminated = "admin_terminated";
    public const string BrowserNotProxied = "browser_not_proxied";
    public const string ProtocolError = "protocol_error";

    public static string ToWire(TunnelEndReason reason) => reason switch
    {
        TunnelEndReason.GuestEnded => GuestEnded,
        TunnelEndReason.HostEnded => HostEnded,
        TunnelEndReason.Expired => Expired,
        TunnelEndReason.GuestDisconnected => GuestDisconnected,
        TunnelEndReason.HostDisconnected => HostDisconnected,
        TunnelEndReason.ConnectFailed => ConnectFailed,
        TunnelEndReason.AdminTerminated => AdminTerminated,
        TunnelEndReason.BrowserNotProxied => BrowserNotProxied,
        TunnelEndReason.ProtocolError => ProtocolError,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown end reason"),
    };

    /// <summary>Turns a <c>session.terminate</c> reason from the server into a <see cref="TunnelEndReason"/>; an unknown one becomes <see cref="TunnelEndReason.ProtocolError"/>.</summary>
    public static bool TryParse(string? wire, out TunnelEndReason reason)
    {
        switch (wire?.Trim().ToLowerInvariant())
        {
            case GuestEnded: reason = TunnelEndReason.GuestEnded; return true;
            case HostEnded: reason = TunnelEndReason.HostEnded; return true;
            case Expired: reason = TunnelEndReason.Expired; return true;
            case GuestDisconnected: reason = TunnelEndReason.GuestDisconnected; return true;
            case HostDisconnected: reason = TunnelEndReason.HostDisconnected; return true;
            case ConnectFailed: reason = TunnelEndReason.ConnectFailed; return true;
            case AdminTerminated: reason = TunnelEndReason.AdminTerminated; return true;
            case BrowserNotProxied: reason = TunnelEndReason.BrowserNotProxied; return true;
            case ProtocolError: reason = TunnelEndReason.ProtocolError; return true;
            default: reason = TunnelEndReason.ProtocolError; return false;
        }
    }

    public static TunnelEndReason Parse(string? wire)
    {
        TryParse(wire, out var reason);
        return reason;
    }

    /// <summary>The end reason belonging to this side when the user stops the session themselves.</summary>
    public static TunnelEndReason LocalEnd(TunnelRole role) => role == TunnelRole.Host ? TunnelEndReason.HostEnded : TunnelEndReason.GuestEnded;

    /// <summary>The end reason when this side vanishes (the control channel drops): the server names it after whoever vanished.</summary>
    public static TunnelEndReason LocalDisconnect(TunnelRole role) => role == TunnelRole.Host ? TunnelEndReason.HostDisconnected : TunnelEndReason.GuestDisconnected;
}
