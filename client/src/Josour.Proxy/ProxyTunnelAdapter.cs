using System.Net;
using Josour.Core.Net;
using Josour.Tunnel;

namespace Josour.Proxy;

/// <summary>
/// The guest's side of <see cref="TunnelSession"/>: it builds a <see cref="ConnectProxyServer"/> over the mux and exposes its port, the check page's URL
/// and the signal that it was reached. The session exposes those three to the application in <c>Proxy</c> and <c>ProbeSeen</c> so it can point the
/// system proxy at the port and confirm a browser came through.
///
/// Why a factory: <c>Josour.Proxy</c> depends on <c>Josour.Tunnel</c> (the mux), so the direction cannot be reversed.
/// The application passes <c>ProxyTunnelAdapter.Create</c>.
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

    /// <summary>The production use: <c>GuestProxy = ProxyTunnelAdapter.Create</c>, with this platform's admission check.</summary>
    public static ITunnelProxy Create(TunnelProxyContext context) => Create(context, DefaultAdmission());

    /// <param name="admission">Which local processes may connect; null = every local connection is accepted (the tests and the Spike tool).</param>
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
        IConnectionAdmission? admission,
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
            Admission = admission,
            AddressBlocker = addressBlocker,
            SystemProxy = systemProxy ?? SystemProxyResolver.Default,
        };
        return new ProxyTunnelAdapter(new ConnectProxyServer(options));
    }

    /// <summary>
    /// The account check this platform has: Windows can tell another account's process apart (<see cref="SameUserAdmission"/>).
    /// macOS relies on the loopback listener alone; asking lsof for every connection of a whole machine's browsing costs a
    /// process per connection.
    /// </summary>
    public static IConnectionAdmission? DefaultAdmission() => OperatingSystem.IsWindows() ? new SameUserAdmission() : null;

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
