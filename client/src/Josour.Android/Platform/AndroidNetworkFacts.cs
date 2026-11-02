using System.Net;
using Android.Content;
using Android.Net;
using Josour.Infrastructure.Control;
using Josour.Mobile.Platform;
using Josour.Tunnel.Candidates;

namespace Josour.Droid.Platform;

/// <summary>
/// The phone's networks from <see cref="ConnectivityManager"/>: which one carries the traffic (Wi-Fi or mobile data, and
/// whether a VPN sits on top), and — for the egress policy — every address, on-link prefix and gateway of every network the
/// phone is attached to. Both Wi-Fi and mobile data are usually up at once; the guest must reach neither network's
/// neighbours, whichever happens to be the default.
/// <para>
/// A change of the default network (walking out of Wi-Fi range onto mobile data, or back) raises
/// <see cref="ConnectivitySignal.NetworkAddressChanged"/>, so the control channel reconnects on the new network at once
/// instead of waiting out a dead socket.
/// </para>
/// </summary>
internal sealed class AndroidNetworkFacts : ConnectivityManager.NetworkCallback, INetworkFacts
{
    private readonly ConnectivityManager _connectivity;
    private readonly object _gate = new();
    private NetworkSnapshot _current = NetworkSnapshot.Offline;
    private Network? _default;

    public AndroidNetworkFacts(Context context)
    {
        _connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        _current = Read();
        _default = _connectivity.ActiveNetwork;
        _connectivity.RegisterDefaultNetworkCallback(this);
    }

    public NetworkSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<NetworkSnapshot>? Changed;

    public event Action<ConnectivitySignal>? Signalled;

    public override void OnAvailable(Network network)
    {
        var moved = !Equals(network, _default);
        _default = network;
        Refresh(moved);
    }

    public override void OnLost(Network network)
    {
        if (Equals(network, _default)) _default = null;
        Refresh(signal: true);
    }

    public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities) => Refresh(signal: false);

    public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => Refresh(signal: true);

    private void Refresh(bool signal)
    {
        NetworkSnapshot snapshot;
        try
        {
            snapshot = Read();
        }
        catch (Exception)
        {
            return; // a network that vanished while being read; the next callback brings the truth
        }

        bool changed;
        lock (_gate)
        {
            changed = !SameAs(_current, snapshot);
            _current = snapshot;
        }

        if (changed) Changed?.Invoke(snapshot);
        if (signal || changed) Signalled?.Invoke(ConnectivitySignal.NetworkAddressChanged);
    }

    private NetworkSnapshot Read()
    {
        var active = _connectivity.ActiveNetwork;
        var activeCaps = active is null ? null : _connectivity.GetNetworkCapabilities(active);
        if (activeCaps is null || !activeCaps.HasCapability(NetCapability.Internet))
        {
            return NetworkSnapshot.Offline with { Addresses = Array.Empty<IPAddress>(), Networks = Array.Empty<IPNetwork>() };
        }

        var isVpn = activeCaps.HasTransport(TransportType.Vpn);
        var kind = KindOf(activeCaps);
        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();

#pragma warning disable CA1422 // GetAllNetworks: still the one call that lists every attached network
        foreach (var network in _connectivity.GetAllNetworks())
#pragma warning restore CA1422
        {
            var caps = _connectivity.GetNetworkCapabilities(network);
            // Under a VPN the default is the VPN itself; what it rides on is the network the phone really uses.
            if (kind is NetworkKind.Other && caps is not null && !caps.HasTransport(TransportType.Vpn) && caps.HasCapability(NetCapability.Internet))
            {
                kind = KindOf(caps);
            }

            var link = _connectivity.GetLinkProperties(network);
            if (link is null) continue;
            foreach (var linkAddress in link.LinkAddresses)
            {
                if (ToIp(linkAddress.Address) is not { } ip) continue;
                Add(addresses, ip);
                if (LocalNetwork.PolicyNetworkFor(ip, linkAddress.PrefixLength) is { } onLink && !networks.Contains(onLink))
                {
                    networks.Add(onLink);
                }
            }

            foreach (var route in link.Routes)
            {
                // The gateways: the Wi-Fi router above all, which .NET on Android cannot see (no readable routing table).
                if (route.Gateway is { } gateway && ToIp(gateway) is { } ip && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                {
                    Add(addresses, ip);
                }
            }
        }

        return new NetworkSnapshot(kind, isVpn, addresses, networks);
    }

    private static NetworkKind KindOf(NetworkCapabilities caps)
    {
        if (caps.HasTransport(TransportType.Wifi)) return NetworkKind.WiFi;
        if (caps.HasTransport(TransportType.Cellular)) return NetworkKind.Cellular;
        if (caps.HasTransport(TransportType.Ethernet)) return NetworkKind.Ethernet;
        return NetworkKind.Other;
    }

    private static IPAddress? ToIp(Java.Net.InetAddress? address)
    {
        var bytes = address?.GetAddress();
        if (bytes is null || bytes.Length is not (4 or 16)) return null;
        var ip = new IPAddress(bytes);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static void Add(List<IPAddress> list, IPAddress ip)
    {
        if (!list.Contains(ip)) list.Add(ip);
    }

    private static bool SameAs(NetworkSnapshot a, NetworkSnapshot b) =>
        a.Kind == b.Kind && a.IsVpn == b.IsVpn && a.Addresses.SequenceEqual(b.Addresses) && a.Networks.SequenceEqual(b.Networks);
}
