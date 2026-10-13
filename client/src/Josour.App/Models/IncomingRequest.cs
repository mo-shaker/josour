using Josour.Core.Control;
using Josour.Infrastructure.Session;

namespace Josour.App.Models;

/// <summary>
/// What the host is asked to approve, and everything the product document (section 15) says must be on screen before the
/// answer: who is asking (<see cref="GuestUserId"/>/<see cref="GuestDeviceId"/> identify them; the two names are only
/// what they call themselves), from which device, for how long, which sites that request's allow-list version permits, and —
/// as fixed text in the window — that the sites will see the host's public IP and that the host can disconnect at any time.
/// Built from <c>request.incoming</c> or from the debug simulator.
/// </summary>
public sealed record IncomingRequest(
    Guid RequestId,
    Guid GuestUserId,
    Guid GuestDeviceId,
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
        return new IncomingRequest(
            message.RequestId,
            message.GuestUserId,
            message.GuestDeviceId,
            message.GuestName,
            message.GuestDevice,
            message.DurationMin,
            message.ExpiresAt,
            allowlist);
    }
}

/// <summary>The host's answer: what it decided, and whether it also let this contact in unasked from now on.</summary>
/// <param name="AlwaysAllow">
/// "Always let this contact use my connection without asking" (ADR-0014). Only ever true alongside
/// <see cref="IncomingRequestDecision.Accepted"/>, and stored on the server only after the acceptance was sent.
/// </param>
public sealed record IncomingRequestAnswer(IncomingRequestDecision Decision, bool AlwaysAllow = false)
{
    public static IncomingRequestAnswer Of(IncomingRequestDecision decision) => new(decision);
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
