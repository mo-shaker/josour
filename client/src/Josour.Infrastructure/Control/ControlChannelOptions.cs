using System.Net;

namespace Josour.Infrastructure.Control;

/// <summary>
/// Timing, transport and backoff knobs of <see cref="ControlChannel"/>. The defaults are the contract's
/// (docs/ws-protocol.md sections 1 and 7): <c>hello</c> within 5 s, ping every 20 s, dead after 40 s,
/// reconnect 1, 2, 4, 8, 16, 30 s. Tests shrink the intervals and replace <see cref="BackoffDelay"/> and
/// <see cref="NextJitter"/> so a reconnect is instant and deterministic.
/// </summary>
public sealed record ControlChannelOptions
{
    public static ControlChannelOptions Default { get; } = new();

    /// <summary>Path appended to the server base URL (with its optional path prefix): <c>https://host/rb/</c> → <c>wss://host/rb/ws</c>.</summary>
    public string Path { get; init; } = "ws";

    /// <summary>TCP + WebSocket handshake timeout.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for <c>hello.ack</c> after sending <c>hello</c> (the server allows itself 5 s to see our hello).</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>We send <c>ping</c> this often (contract: 20 s).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>No frame at all from the server for this long ⇒ the connection is dead (contract: two missed beats, 40 s).</summary>
    public TimeSpan DeadInterval { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>Reconnect delays; the last one repeats forever.</summary>
    public IReadOnlyList<TimeSpan> BackoffSteps { get; init; } = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    };

    /// <summary>Up to this fraction is added on top of a backoff step, so many clients do not return at the same instant.</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>Source of the jitter in [0,1); tests pin it.</summary>
    public Func<double> NextJitter { get; init; } = () => Random.Shared.NextDouble();

    /// <summary>The backoff wait itself, so tests do not spend real seconds in it.</summary>
    public Func<TimeSpan, CancellationToken, Task> BackoffDelay { get; init; } = (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Largest single frame we assemble; a larger one is dropped (and logged) instead of growing memory.</summary>
    public int MaxFrameBytes { get; init; } = 512 * 1024;

    /// <summary>Route the WebSocket through the machine's proxy configuration (plan 8.5: corporate proxies).</summary>
    public bool UseSystemProxy { get; init; } = true;

    /// <summary>Explicit proxy; when null and <see cref="UseSystemProxy"/> is set, the system proxy is used.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>How long <see cref="ControlChannel.DisposeAsync"/> waits for the close handshake before dropping the socket.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
