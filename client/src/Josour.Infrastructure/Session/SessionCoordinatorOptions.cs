namespace Josour.Infrastructure.Session;

/// <summary>
/// The knobs of <see cref="SessionCoordinator"/>. Every default is the production value from docs/ws-protocol.md and
/// docs/protocol.md; the tests shorten them and replace <see cref="Post"/> so property changes are observed inline.
/// </summary>
public sealed record SessionCoordinatorOptions
{
    public static SessionCoordinatorOptions Default { get; } = new();

    /// <summary>
    /// How the observable properties reach the UI thread. The default runs inline (tests, head-less use); the Avalonia app passes
    /// its dispatcher post so that bindings are updated on the UI thread.
    /// </summary>
    public Action<Action> Post { get; init; } = action => action();

    /// <summary>
    /// How long the guest waits for the check page to come through the local proxy after opening it in the default browser.
    /// Nothing within this window is a warning (that browser may ignore the system proxy), never an ending.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary><c>session.stats</c> cadence on the host (docs/ws-protocol.md section 3).</summary>
    public TimeSpan StatsInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget for <c>ITunnelSession.ConnectAsync</c>. The server ends a session that is not <c>active</c> 30 s after
    /// <c>session.created</c> (docs/ws-protocol.md section 5), and <c>hello.ack.settings</c> carries no connect timeout,
    /// so the client uses the same 30 s counted from <c>session.created</c>.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Countdown / stats tick.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for one cleanup step so ending never hangs on a dead peer.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Timeout of the <c>request.create</c> round-trip.</summary>
    public TimeSpan RequestReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Budget for the end-of-session <c>POST /diagnostics</c> that reports unauthenticated listener hits. Shorter than the
    /// API client's own 15 s: the session is already over, so the report may not hold the teardown open.
    /// </summary>
    public TimeSpan DiagnosticsTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A local clock this far from <c>hello.ack.server_time</c> is logged as a warning. It does not change the countdown —
    /// that is anchored on server time and counted monotonically — but it explains TLS validity failures and log lines
    /// that do not line up with the server's.
    /// </summary>
    public TimeSpan ClockSkewWarning { get; init; } = TimeSpan.FromMinutes(1);
}
