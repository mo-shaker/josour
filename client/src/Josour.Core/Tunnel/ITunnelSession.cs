namespace Josour.Core.Tunnel;

/// <summary>
/// One tunnel session between the two machines. Track B provides the implementation in Josour.Tunnel; track C consumes it from the application.
/// The lifecycle: PrepareAsync (a listener + candidates) -> ConnectAsync (once the other side's candidates arrive) -> Connected -> EndAsync.
/// </summary>
public interface ITunnelSession : IAsyncDisposable
{
    Guid SessionId { get; }
    TunnelRole Role { get; }
    TunnelState State { get; }
    event Action<TunnelState>? StateChanged;

    /// <summary>Generates the certificate, opens the listener, requests the UPnP mapping, and returns what is sent in session.endpoint.</summary>
    Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct);

    /// <summary>Connects to the other side's candidates in parallel while still accepting inbound connections. It ends at the first authenticated connection or at the timeout.</summary>
    Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct);

    TunnelStats Stats { get; }

    /// <summary>The distinct domains opened through the tunnel. Filled on the host only.</summary>
    IReadOnlyCollection<string> DomainsSeen { get; }

    /// <summary>
    /// Added in week 3: the local proxy's port and the check page's URL after ConnectAsync on the guest side (null on the host or before connecting).
    /// The application needs them to launch the browser; launching and closing the browser stay its responsibility, not the tunnel's.
    /// </summary>
    GuestProxyInfo? Proxy { get; }

    /// <summary>
    /// What the local proxy counted on the guest side, or null on the host and before connecting. It is read when the check page
    /// times out: without it the log says "the page did not arrive" and cannot tell a browser that never connected from a connection the proxy refused.
    /// </summary>
    IReadOnlyDictionary<string, long>? ProxyCounters { get; }

    /// <summary>
    /// Added in week 3: raised at the check page's first arrival through the proxy (guest only). Its absence after the browser is launched =
    /// the browser is not going through the proxy, which the application reports as browser_not_proxied (docs/ws-protocol.md section 5).
    /// </summary>
    event Action? ProbeSeen;

    /// <summary>
    /// Added in week 3: the tunnel died of its own accord (a drop, or the other side's process dying, or the PONG timeout) rather than by a deliberate end.
    /// The payload is a suggested reason for the application to report to the server. A clean close (EndAsync or a mutual GOAWAY) never raises this event.
    /// </summary>
    event Action<TunnelEndReason>? Died;

    /// <summary>The diagnostic data for the relay decision gate (the candidates tried, and each one's timing and error).</summary>
    IReadOnlyDictionary<string, object?> Diagnostics { get; }

    /// <summary>Carries out the cleanup order in docs/protocol.md section 7 from step 3 onwards.</summary>
    Task EndAsync(TunnelEndReason reason, CancellationToken ct);
}

/// <summary>The raw transport layer. Direct (TCP) today; the relay later, with no change to the authentication or the mux.</summary>
public interface ITunnelTransport
{
    string Name { get; }
    Task<System.IO.Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct);
}
