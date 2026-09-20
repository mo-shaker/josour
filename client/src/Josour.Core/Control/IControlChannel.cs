namespace Josour.Core.Control;

public enum ControlChannelState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>Why the channel stopped for good (no further reconnect attempts). Mirrors docs/ws-protocol.md section 7.</summary>
public enum ControlCloseReason
{
    /// <summary>Local shutdown: <see cref="IAsyncDisposable.DisposeAsync"/> (sign-out, app exit).</summary>
    Disposed,

    /// <summary>Close <c>4401</c> and a freshly refreshed token did not help either.</summary>
    Unauthorized,

    /// <summary>Close <c>4403</c>: the device was revoked or the user disabled.</summary>
    DeviceRevoked,

    /// <summary>Close <c>4409</c>: a newer connection of the same device took over.</summary>
    ReplacedByAnotherConnection,

    /// <summary>The channel cannot run at all (no server configured, no token).</summary>
    Failed,
}

/// <summary>One terminal close of the channel: the reason, the raw close code and the server's description.</summary>
public sealed record ControlChannelClosed(ControlCloseReason Reason, int? CloseCode, string? Description);

/// <summary>
/// The control channel to the server (WSS). Track C provides the implementation in Josour.Infrastructure.
/// It reconnects with exponential backoff and re-announces "available" automatically after reconnecting.
/// </summary>
public interface IControlChannel : IAsyncDisposable
{
    ControlChannelState State { get; }
    event Action<ControlChannelState>? StateChanged;
    event Action<ControlMessage>? MessageReceived;

    /// <summary>Raised once when the channel stops for a reason it will not retry (4401/4403/4409 or an unusable configuration).</summary>
    event Action<ControlChannelClosed>? Closed;

    /// <summary>Opens the connection, sends hello and waits for hello.ack.</summary>
    Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct);

    Task SendAsync(ControlMessage message, CancellationToken ct);

    /// <summary>
    /// Sends a message carrying a ref and waits for the matching reply, or an error with the same ref.
    /// <para>
    /// The real channel surfaces an <c>error</c> with the same <c>ref</c> as <see cref="ControlErrorException"/>;
    /// <c>MockControlChannel</c> (week 2) still completes the call with the <see cref="ErrorMessage"/> itself.
    /// Callers must therefore handle both until the mock is retired.
    /// </para>
    /// </summary>
    Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A control channel that can be told to stop waiting and look at its connection right now.
/// <para>
/// The backoff ladder (1, 2, 4 … 30 s) is the right answer to a server that is down, and the wrong one to a laptop that
/// just woke up or a machine that just changed networks: the reason the connection failed is gone, and waiting out the
/// remaining 30 s is pure dead time in front of the user. <c>ConnectivityWatcher</c> turns those two operating-system
/// events into a call here. It is a separate interface, not a member of <see cref="IControlChannel"/>, so a decorator or
/// a stand-in that has nothing to wake keeps compiling untouched; a caller that holds only an
/// <see cref="IControlChannel"/> tests for it.
/// </para>
/// </summary>
public interface IReconnectNow
{
    /// <summary>
    /// Cuts short the current reconnect wait (the next attempt starts at once and the ladder restarts) and, on a channel
    /// that still believes it is connected, sends a heart-beat immediately so a socket that died while the machine slept
    /// is discovered now rather than at the next interval. Safe to call at any time, from any thread, and never throws.
    /// </summary>
    /// <param name="reason">What woke it, for the log: "power resumed", "network address changed", …</param>
    void ReconnectNow(string reason);
}
