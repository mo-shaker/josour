using System.Net;
using Josour.Core.Tunnel;
using Josour.Egress;
using Josour.Infrastructure.Session;
using Josour.Mobile.Platform;
using Josour.Tunnel;
using Josour.Tunnel.Candidates;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.Services;

/// <summary>
/// The phone's tunnel: the same <see cref="TunnelSession"/> and <see cref="EgressTunnelAdapter"/> the desktop host runs, with
/// three differences that belong to a phone.
/// <list type="bullet">
///   <item>The addresses and on-link networks the guest must not reach are the union of what .NET reads from the interfaces and
///     what Android's <c>LinkProperties</c> report (gateways included), so the router behind the phone's Wi-Fi stays blocked
///     even though .NET on Android cannot see it.</item>
///   <item>No UPnP: a mobile network is behind carrier-grade NAT, and a Wi-Fi router would need a multicast lock for a mapping
///     that rarely helps. The relay is the path (ADR-0009); a direct path still wins between two devices on one network.</item>
///   <item>Host only. A guest needs the operating system's traffic routed through a local proxy, which a phone does with a
///     <c>VpnService</c> this version does not have; the phone never declares the capabilities that would make it a guest.</item>
/// </list>
/// </summary>
public sealed class MobileTunnelSessionFactory : ITunnelSessionFactory
{
    private readonly INetworkFacts _network;
    private readonly ILogger<MobileTunnelSessionFactory> _logger;

    public MobileTunnelSessionFactory(INetworkFacts network, ILogger<MobileTunnelSessionFactory> logger)
    {
        _network = network;
        _logger = logger;
    }

    public ITunnelSession Create(TunnelSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Material.Role != TunnelRole.Host)
        {
            throw new NotSupportedException("This phone can only host a session.");
        }

        var facts = _network.Current;
        var addresses = Union(LocalNetwork.GetPolicyLocalAddresses(), facts.Addresses);
        var networks = Union(LocalNetwork.GetPolicyLocalNetworks(), facts.Networks);

        _logger.LogInformation(
            "Creating the host tunnel for session {SessionId} on {Network}{Vpn}: allow-list version {AllowlistVersion}, allowed ports {AllowedPorts}, {AddressCount} local addresses and {NetworkCount} on-link networks blocked",
            request.Material.SessionId,
            facts.Kind,
            facts.IsVpn ? " (VPN)" : string.Empty,
            request.Allowlist.Version,
            string.Join(",", request.AllowedPorts),
            addresses.Count,
            networks.Count);
        if (request.Relay is { } relay)
            _logger.LogInformation("Relay available for session {SessionId}: {Address}:{Port}", request.Material.SessionId, relay.Address, relay.Port);

        return new TunnelSession(request.Material, BuildOptions(request, addresses, networks));
    }

    /// <summary>The options for one host session; separate so the tests can read exactly what the phone hands the tunnel.</summary>
    internal static TunnelSessionOptions BuildOptions(
        TunnelSessionRequest request,
        IReadOnlyList<IPAddress> blockedAddresses,
        IReadOnlyList<IPNetwork> blockedNetworks) => new()
        {
            Allowlist = request.Allowlist,
            AllowedPorts = request.AllowedPorts,
            OurPublicIp = request.OurPublicIp,
            HostEgress = EgressTunnelAdapter.Create,
            StartGuestProxy = false,
            EnableUpnp = false,
            Relay = request.Relay,
            LocalAddresses = blockedAddresses,
            LocalNetworks = blockedNetworks,
        };

    internal static IReadOnlyList<T> Union<T>(IEnumerable<T> first, IEnumerable<T> second) where T : notnull
    {
        var result = new List<T>();
        foreach (var item in first.Concat(second))
        {
            if (!result.Contains(item)) result.Add(item);
        }
        return result;
    }
}
