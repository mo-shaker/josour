using Microsoft.Extensions.Logging;
using Josour.Core.Tunnel;
using Josour.Egress;
using Josour.Infrastructure.Session;
using Josour.Proxy;
using Josour.Tunnel;

namespace Josour.App.Services;

/// <summary>
/// The one place where the App wires Track B's pieces together: <see cref="SessionCoordinator"/> asks for a tunnel and gets a
/// real <see cref="TunnelSession"/> with the host's egress policy (<see cref="EgressTunnelAdapter"/>), the guest's local proxy
/// (<see cref="ProxyTunnelAdapter"/>, which on Windows admits only processes of the signed-in account) and the system-proxy
/// restore that puts step 2 of docs/protocol.md section 7 in its right place inside <c>EndAsync</c>.
/// <para>
/// It exists because <c>Josour.Infrastructure</c> deliberately does not reference Tunnel/Egress/Proxy: the coordinator is
/// testable with a fake factory, and Egress/Proxy depend on Tunnel (ADR-0006) so the direction cannot be reversed.
/// </para>
/// </summary>
public sealed class TunnelSessionFactory : ITunnelSessionFactory
{
    private readonly ILogger<TunnelSessionFactory> _logger;

    public TunnelSessionFactory(ILogger<TunnelSessionFactory> logger) => _logger = logger;

    public ITunnelSession Create(TunnelSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new TunnelSessionOptions
        {
            Allowlist = request.Allowlist,
            AllowedPorts = request.AllowedPorts,
            OurPublicIp = request.OurPublicIp,
            HostEgress = EgressTunnelAdapter.Create,
            GuestProxy = ProxyTunnelAdapter.Create,
            RestoreSystemProxyAsync = request.RestoreSystemProxyAsync,
            Relay = request.Relay,
        };

        _logger.LogInformation(
            "Creating the {Role} tunnel for session {SessionId}: allow-list version {AllowlistVersion} ({EntryCount} entries), allowed ports {AllowedPorts}",
            request.Material.Role,
            request.Material.SessionId,
            request.Allowlist.Version,
            request.Allowlist.Entries.Count,
            string.Join(",", request.AllowedPorts));
        // The address only; the token is a bearer credential and never reaches a log.
        if (request.Relay is { } relay)
            _logger.LogInformation("Relay available for session {SessionId}: {Address}:{Port}", request.Material.SessionId, relay.Address, relay.Port);

        return new TunnelSession(request.Material, options);
    }
}
