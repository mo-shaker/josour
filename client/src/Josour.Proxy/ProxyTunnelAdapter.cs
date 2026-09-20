using System.Net;
using Josour.Core.Browser;
using Josour.Core.Net;
using Josour.Tunnel;

namespace Josour.Proxy;

/// <summary>
/// The guest's side of <see cref="TunnelSession"/>: it builds a <see cref="ConnectProxyServer"/> over the mux and exposes its port, the check page's URL
/// and the signal that it was reached. The session exposes those three to the application in <c>Proxy</c> and <c>ProbeSeen</c> so it can launch the browser and detect
/// <c>browser_not_proxied</c>.
///
/// Why a factory: <c>Josour.Proxy</c> depends on <c>Josour.Tunnel</c> (the mux), so the direction cannot be reversed.
/// Track C passes <c>ProxyTunnelAdapter.Create</c>, or <c>ctx =&gt; ProxyTunnelAdapter.Create(ctx, browser)</c> to enable the owning-PID check.
/// </summary>
public sealed class ProxyTunnelAdapter : ITunnelProxy
{
    private readonly ConnectProxyServer _server;

    public ProxyTunnelAdapter(ConnectProxyServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _server.ProbeHit += OnProbeHit;
    }

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["accepted"] = _server.Counters.Accepted,
        ["rejected_by_owner"] = _server.Counters.RejectedByOwner,
        ["tunneled"] = _server.Counters.Tunneled,
        ["direct"] = _server.Counters.DirectConnects,
        ["direct_http"] = _server.Counters.DirectHttpRequests,
        ["rejected"] = _server.Counters.Rejected,
        ["probe_hits"] = _server.Counters.ProbeHits,
        ["errors"] = _server.Counters.Errors,
        ["via_system_proxy"] = _server.Counters.ViaSystemProxy,
    };

    /// <summary>The production use with no owner check: <c>GuestProxy = ProxyTunnelAdapter.Create</c>.</summary>
    public static ITunnelProxy Create(TunnelProxyContext context) => Create(context, null);

    /// <param name="browser">The browser session for the owning-PID check; null = every local connection is accepted (the tests and the Spike tool).</param>
    /// <param name="ownerPidChecker">A custom PID checker; null = the default.</param>
    /// <param name="addressBlocker">
    /// Replacing the address-blocking check on the direct path; for in-process tests only (to permit 127.0.0.1).
    /// null = the <c>IpRangePolicy</c> policy (the counterpart of <c>EgressTunnelAdapter.Create</c> on the host).
    /// </param>
    /// <param name="systemProxy">
    /// The system proxy for the direct path (plan 8.5). null = the machine's real settings.
    /// In-process tests pass <see cref="NoSystemProxy.Instance"/> so they are not affected by the developer machine's settings.
    /// </param>
    public static ITunnelProxy Create(
        TunnelProxyContext context,
        IBrowserSession? browser,
        IOwnerPidChecker? ownerPidChecker = null,
        Func<IPAddress, bool>? addressBlocker = null,
        ISystemProxyResolver? systemProxy = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new ConnectProxyOptions
        {
            Allowlist = context.Allowlist,
            AllowedPorts = context.AllowedPorts,
            Resolver = context.Resolver,
            PeerPublicIp = context.PeerPublicIp,
            Mux = context.Mux,
            Browser = browser,
            // The default must match the platform, not the tests. PermissiveOwnerPidChecker answers
            // "I don't know" to every question, and on Windows an unknown owner is refused - so wiring a
            // browser without also wiring a checker refused every connection the browser made, which is
            // exactly what shipped: accepted=33 rejected_by_owner=33 and a session that could not browse.
            OwnerPidChecker = ownerPidChecker ?? DefaultOwnerPidChecker(),
            AddressBlocker = addressBlocker,
            SystemProxy = systemProxy ?? SystemProxyResolver.Default,
        };
        return new ProxyTunnelAdapter(new ConnectProxyServer(options));
    }

    /// <summary>
    /// The checker that can actually answer on this platform (<see cref="OwnerPidCheckers.ForCurrentPlatform"/>).
    /// Only Windows has one. Elsewhere this returns the permissive checker and the proxy then refuses to start,
    /// which is the intended outcome: a guest role that cannot tell the work browser from the rest of the machine
    /// is not a guest role, and pretending otherwise would hand the host's address to every program on it.
    /// </summary>
    private static IOwnerPidChecker DefaultOwnerPidChecker() => OwnerPidCheckers.ForCurrentPlatform();

    /// <summary>The underlying server (the detailed counters, and when the check page was first reached).</summary>
    public ConnectProxyServer Server => _server;

    public int Port => _server.Port;
    public string ProbeUrl => ProbePage.Url;

    public event Action? ProbeSeen;

    public void Start() => _server.Start();
    public void StopAccepting() => _server.StopAccepting();
    public Task StopAsync() => _server.StopAsync();

    public async ValueTask DisposeAsync()
    {
        _server.ProbeHit -= OnProbeHit;
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private void OnProbeHit(DateTimeOffset _)
    {
        try { ProbeSeen?.Invoke(); } catch { /* the listener is responsible for its own errors */ }
    }
}
