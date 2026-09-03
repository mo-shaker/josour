using RouteBridge.Core.Control;

namespace RouteBridge.App.Models;

/// <summary>What the host is asked to approve. Built from <c>request.incoming</c> (week 3+) or from the debug simulator.</summary>
public sealed record IncomingRequest(
    Guid RequestId,
    string GuestName,
    string GuestDevice,
    int DurationMinutes,
    DateTimeOffset ExpiresAt,
    string AllowedSitesSummary)
{
    /// <summary>Maps the wire message; the allow-list text is resolved separately (week 5: GET /domains?version=N).</summary>
    public static IncomingRequest FromMessage(RequestIncomingMessage message, string allowedSitesSummary) =>
        new(message.RequestId, message.GuestName, message.GuestDevice, message.DurationMin, message.ExpiresAt, allowedSitesSummary);
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
