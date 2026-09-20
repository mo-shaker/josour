using System.Net;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel;

/// <summary>
/// The source of the candidates and the owner of the UPnP mapping. <see cref="CandidateGatherer"/> implements it; the tests replace it with a fixed source.
/// </summary>
public interface ICandidateSource : IAsyncDisposable
{
    /// <summary>Is there an active UPnP mapping that must be removed at cleanup?</summary>
    bool HasMapping { get; }

    Task<GatherResult> GatherAsync(GatherOptions options, CancellationToken ct);

    /// <summary>Cleanup step 4 (docs/protocol.md section 7). It does not throw; false on failure or when there is no mapping.</summary>
    Task<bool> RemoveMappingAsync(CancellationToken ct);
}

/// <summary>What the egress policy on the host needs. <see cref="TunnelSession"/> builds it and passes it to the egress factory.</summary>
/// <param name="BlockedLocalAddresses">The host's interface addresses, its gateways and its public address as the server sees it (docs/protocol.md section 6 rule 6).</param>
/// <param name="MaxConcurrentStreams">
/// The concurrent stream limit that goes with the RTT-derived window (docs/protocol.md section 5: 256 at 1 MiB,
/// 128 at 2 MiB, 64 at 4 MiB). The default is 256 for whoever builds the context by hand with no derivation. The 50 opens/second limit does not change.
/// </param>
public sealed record TunnelEgressContext(
    IAllowlist Allowlist,
    IReadOnlyList<int> AllowedPorts,
    IHostResolver Resolver,
    IReadOnlyList<IPAddress> BlockedLocalAddresses,
    IMuxAcceptor Acceptor,
    int MaxConcurrentStreams = MuxWindow.DefaultMaxConcurrentStreams);

/// <summary>What the local proxy on the guest side needs. <see cref="TunnelSession"/> builds it and passes it to the proxy factory.</summary>
/// <param name="PeerPublicIp">The host's public IP as the server sees it; it appears on the check page.</param>
public sealed record TunnelProxyContext(
    IAllowlist Allowlist,
    IReadOnlyList<int> AllowedPorts,
    IHostResolver Resolver,
    string PeerPublicIp,
    IMuxConnection Mux);

/// <summary>
/// The host's side: the egress policy and the counters wired to an <see cref="IMuxAcceptor"/>.
/// The real implementation is <c>Josour.Egress.EgressTunnelAdapter</c>; it is injected as a factory because Egress depends on Tunnel and not the other way round.
/// </summary>
public interface ITunnelEgress : IAsyncDisposable
{
    /// <summary>Registers the OPEN handler on the acceptor. Called once.</summary>
    void Attach(IMuxAcceptor acceptor);

    /// <summary>The bytes from the guest to the sites (net payload, with no tunnel headers).</summary>
    long BytesUp { get; }

    /// <summary>The bytes from the sites to the guest.</summary>
    long BytesDown { get; }

    /// <summary>The normalised distinct domains for session.end.domains.</summary>
    IReadOnlyCollection<string> Domains { get; }
}

/// <summary>
/// The guest's side: a local proxy over the tunnel. The real implementation is <c>Josour.Proxy.ProxyTunnelAdapter</c>
/// (injected as a factory for the same reason: Proxy depends on Tunnel and not the other way round).
/// </summary>
public interface ITunnelProxy : IAsyncDisposable
{
    /// <summary>The listening port on 127.0.0.1 (known the moment it is created, before Start).</summary>
    int Port { get; }

    /// <summary>The check page's URL, which the browser is opened on.</summary>
    string ProbeUrl { get; }

    /// <summary>The check page's first arrival (and anything after it).</summary>
    event Action? ProbeSeen;

    /// <summary>
    /// The proxy's counters, for the one moment they matter: the check page did not arrive and nobody knows why.
    /// "Zero accepted and one connection refused for its owner" and "nothing was accepted at all" are two entirely different failures with different fixes,
    /// and without these numbers they look like one line in the log: "the page did not arrive".
    /// </summary>
    IReadOnlyDictionary<string, long> Counters { get; }

    void Start();

    /// <summary>Cleanup step 1: no new connections; the ones under way continue.</summary>
    void StopAccepting();

    /// <summary>A full stop, waiting for the connections under way.</summary>
    Task StopAsync();
}

/// <summary>
/// The pieces injectable into <see cref="TunnelSession"/>. Every value has a sensible production default except the two factories:
/// the host needs <see cref="HostEgress"/> and the guest needs <see cref="GuestProxy"/> (unless <see cref="StartGuestProxy"/> is turned off).
/// </summary>
public sealed record TunnelSessionOptions
{
    /// <summary>The site list loaded from the server. The default is an empty list (everything direct on the guest, and everything refused on the host).</summary>
    public IAllowlist Allowlist { get; init; } = new AllowlistMatcher(0, Array.Empty<AllowlistEntry>());

    /// <summary>allowed_ports from hello.ack.settings.</summary>
    public IReadOnlyList<int> AllowedPorts { get; init; } = new[] { 80, 443 };

    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;

    /// <summary>The raw transport for the direct candidates.</summary>
    public ITunnelTransport Transport { get; init; } = new DirectTransport();

    /// <summary>
    /// What arrived in <c>session.created.relay</c>: the relay's address and this side's token. null = no relay in this deployment,
    /// so direct stays alone as it was before ADR-0009. When it is present, it is attempted <b>in parallel</b> with the direct candidates.
    /// The token is a bearer statement: it is not logged and does not appear in the diagnostics.
    /// </summary>
    public RelayEndpointInfo? Relay { get; init; }

    /// <summary>This machine's public IP as the server sees it (hello.ack.public_ip): a public candidate, and blocked in the egress policy.</summary>
    public string? OurPublicIp { get; init; }

    public int ListenPort { get; init; }

    /// <summary>The listener's bind address. null = [::]:0 in DualMode (production); the tests bind on loopback.</summary>
    public IPAddress? BindAddress { get; init; }

    public bool EnableUpnp { get; init; } = true;

    /// <summary>The UPnP mapping's lifetime = the time remaining until expires_at + this slack (docs/protocol.md section 2).</summary>
    public TimeSpan MappingLifetimeSlack { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The UPnP gateway discovery timeout in the default source (4 seconds by default). Ignored with a custom source.</summary>
    public TimeSpan? UpnpDiscoveryTimeout { get; init; }

    /// <summary>The candidate source. null = the real <see cref="CandidateGatherer"/> (Mono.Nat).</summary>
    public Func<ICandidateSource>? CandidateSource { get; init; }

    /// <summary>This machine's addresses, blocked on the host. null = read from the interfaces at PrepareAsync.</summary>
    public IReadOnlyList<IPAddress>? LocalAddresses { get; init; }

    public MuxOptions? Mux { get; init; }

    /// <summary>The egress-policy factory on the host (<c>EgressTunnelAdapter.Create</c>).</summary>
    public Func<TunnelEgressContext, ITunnelEgress>? HostEgress { get; init; }

    /// <summary>The local proxy factory on the guest (<c>ProxyTunnelAdapter.Create</c>).</summary>
    public Func<TunnelProxyContext, ITunnelProxy>? GuestProxy { get; init; }

    /// <summary>false = no proxy on the guest (the self-hosted mode in the Spike tool, and the tunnel-only tests).</summary>
    public bool StartGuestProxy { get; init; } = true;

    /// <summary>
    /// Cleanup step 2 (docs/protocol.md section 7): the polite browser close, then the Job Object. The tunnel does not own the browser,
    /// so the application passes its close in here to be carried out in its right place in the order: after the proxy stops accepting and before the GOAWAY.
    /// Its errors are swallowed and recorded in Diagnostics and do not stop the rest of the cleanup.
    /// </summary>
    public Func<CancellationToken, Task>? CloseBrowserAsync { get; init; }

    /// <summary>The timeout for each closing step in EndAsync, so ending a session does not hang on a dead peer.</summary>
    public TimeSpan CleanupStepTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
