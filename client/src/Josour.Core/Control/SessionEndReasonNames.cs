using Josour.Core.Tunnel;

namespace Josour.Core.Control;

/// <summary>
/// أسماء أسباب الإنهاء على السلك كما في docs/ws-protocol.md القسم 5 (<c>session.end.reason</c> و<c>session.terminate.reason</c>).
/// الجدول في القسم 3 يذكر <c>guest_ended</c>/<c>host_ended</c> فقط لأنها الحالة المعتادة، بينما القسم 5 يعدّد بقية الأسباب
/// (<c>expired</c>، <c>browser_not_proxied</c>، …) والعميل يحتاج أن يبلّغ بها حرفيًا. الاسم هنا هو صيغة القسم 5.
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

    /// <summary>يحوّل سبب <c>session.terminate</c> القادم من الخادم إلى <see cref="TunnelEndReason"/>؛ المجهول يصير <see cref="TunnelEndReason.ProtocolError"/>.</summary>
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

    /// <summary>سبب الإنهاء الذي يخص هذا الطرف عندما يوقف المستخدم الجلسة بنفسه.</summary>
    public static TunnelEndReason LocalEnd(TunnelRole role) => role == TunnelRole.Host ? TunnelEndReason.HostEnded : TunnelEndReason.GuestEnded;

    /// <summary>سبب الإنهاء عندما يختفي هذا الطرف (انقطاع قناة التحكم): الخادم يسميه باسم من اختفى.</summary>
    public static TunnelEndReason LocalDisconnect(TunnelRole role) => role == TunnelRole.Host ? TunnelEndReason.HostDisconnected : TunnelEndReason.GuestDisconnected;
}
