using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.App.Models;

/// <summary>
/// What the host is asked to approve, and everything the product document (section 15) says must be on screen before the
/// answer: who is asking, from which device, for how long, which sites that request's allow-list version permits, and —
/// as fixed text in the window — that the sites will see the host's public IP and that the host can disconnect at any time.
/// Built from <c>request.incoming</c> or from the debug simulator.
/// </summary>
public sealed record IncomingRequest(
    Guid RequestId,
    string GuestName,
    string GuestDevice,
    int DurationMinutes,
    DateTimeOffset ExpiresAt,
    AllowlistDisclosure Allowlist)
{
    /// <summary>
    /// Maps the wire message. The allow-list is resolved separately (<c>GET /domains?version=N</c> through
    /// <see cref="IAllowlistDisclosure"/>) because <c>request.incoming</c> carries only its version number.
    /// </summary>
    public static IncomingRequest FromMessage(RequestIncomingMessage message, AllowlistDisclosure allowlist)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new IncomingRequest(message.RequestId, message.GuestName, message.GuestDevice, message.DurationMin, message.ExpiresAt, allowlist);
    }
}

/// <summary>Outcome of presenting an incoming request to the host.</summary>
public enum IncomingRequestDecision
{
    /// <summary>Host pressed Accept (window or toast).</summary>
    Accepted,

    /// <summary>Host pressed Reject (window or toast).</summary>
    Rejected,

    /// <summary>The 60 s countdown ran out (or the server sent <c>request.expired</c>).</summary>
    TimedOut,

    /// <summary>Host closed the window without answering, or the app cancelled the prompt.</summary>
    Dismissed,
}
