using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Josour.Core.Net;

/// <summary>The confidence of the VPN detection. See <see cref="VpnDetector"/> for the rules.</summary>
public enum VpnConfidence
{
    /// <summary>No VPN interface is up.</summary>
    None = 0,

    /// <summary>A VPN interface exists and is up but does not hold the egress route: the traffic most likely does not pass through it.</summary>
    Low = 1,

    /// <summary>A VPN interface holds the default egress route: outbound traffic really does pass through it.</summary>
    High = 2,
}

/// <summary>A network interface as the detector sees it. Fakeable in the tests.</summary>
/// <param name="Name">The interface's name (utun3, wg0, Ethernet 2).</param>
/// <param name="Description">The product description (WireGuard Tunnel, TAP-Windows Adapter V9).</param>
/// <param name="IsUp">Whether the interface is up.</param>
/// <param name="IsTunnelType">Whether its type is Tunnel/Ppp per the system.</param>
/// <param name="UnicastAddresses">The interface's addresses.</param>
/// <param name="GatewayAddresses">The interface's gateways (having one is necessary for holding a default route, not sufficient).</param>
public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    bool IsUp,
    bool IsTunnelType,
    IReadOnlyList<IPAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> GatewayAddresses);

/// <summary>The source of the interfaces and the egress route. Injected in the tests.</summary>
public interface INetworkAdapterSource
{
    IReadOnlyList<NetworkAdapterInfo> GetAdapters();

    /// <summary>
    /// The source address the system chooses for going out to the internet, or null if it cannot be determined.
    /// Matching against it is how we learn which interface holds the default route without reading the routing table.
    /// </summary>
    IPAddress? GetOutboundSourceAddress();
}

/// <summary>The detection's result: whether there is a VPN, which interface, at what confidence, and why.</summary>
public sealed record VpnDetectionResult(
    VpnConfidence Confidence,
    string? AdapterName,
    string? AdapterDescription,
    string? Marker,
    bool HoldsDefaultRoute)
{
    public static VpnDetectionResult NotDetected { get; } = new(VpnConfidence.None, null, null, null, false);

    public bool IsVpn => Confidence != VpnConfidence.None;

    /// <summary>Should the user be warned that the egress address will be the VPN's? (High confidence only.)</summary>
    public bool ShouldWarn => Confidence == VpnConfidence.High;

    /// <summary>A short diagnostic line for the log and for <c>hello.diagnostics</c>.</summary>
    public string Describe() => Confidence == VpnConfidence.None
        ? "none"
        : $"{Confidence.ToString().ToLowerInvariant()}:{AdapterName}" + (Marker is null ? string.Empty : $" ({Marker})");
}

/// <summary>
/// Detecting VPN interfaces on the host (plan 8.5 and the risk table: "the host on a VPN: the exit IP is the VPN's IP").
///
/// <para><b>The rules:</b> an interface is a candidate if it is <b>up</b> and (its type is Tunnel/Ppp or its name/description matched one of
/// <see cref="Markers"/>) and it matched none of <see cref="Exclusions"/>. A candidate that holds <b>the outbound source address</b>
/// (that is, the one traffic actually leaves by) has <see cref="VpnConfidence.High"/>; merely existing is <see cref="VpnConfidence.Low"/>.</para>
///
/// <para><b>Why the exclusions:</b> Windows classifies Teredo, ISATAP and 6to4 as <c>NetworkInterfaceType.Tunnel</c>
/// and they are not VPNs; and macOS opens <c>utun</c> interfaces for Handoff and iCloud Private Relay with no VPN at all. Without these
/// exclusions the detection becomes a permanent false warning on ordinary machines.</para>
///
/// <para><b>Why the short markers are matched as words:</b> "tun", "tap" and "wg" are common fragments inside other words
/// (Fortune, Adaptap), so they are matched on word boundaries only; the long markers are matched as substrings.</para>
/// </summary>
public static class VpnDetector
{
    /// <summary>Markers matched as a substring (long enough not to collide with other words).</summary>
    private static readonly string[] SubstringMarkers =
    {
        "vpn", "wireguard", "tailscale", "openvpn", "zerotier", "anyconnect", "globalprotect",
        "forticlient", "fortissl", "pulse secure", "sonicwall", "check point", "checkpoint",
        "softether", "mullvad", "nordlynx", "proton", "zscaler", "netbird", "twingate",
        "wintun", "tunnelbear", "hamachi", "ipsec", "l2tp", "pptp", "sstp", "cloudflare warp",
    };

    /// <summary>Markers matched as a whole word or with a numeric suffix (utun3, wg0, tun0, tap-windows).</summary>
    private static readonly string[] TokenMarkers = { "tun", "utun", "tap", "wg", "ppp", "gpd", "nordvpn" };

    /// <summary>Tunnel interfaces that are not VPNs.</summary>
    private static readonly string[] Exclusions =
    {
        "teredo", "isatap", "6to4", "loopback", "pseudo-interface", "bluetooth", "ipv6 helper",
    };

    /// <summary>The markers shown in the documentation and the tests.</summary>
    public static IReadOnlyList<string> Markers { get; } = SubstringMarkers.Concat(TokenMarkers).ToArray();

    /// <summary>Detection using the system's real interfaces.</summary>
    public static VpnDetectionResult Detect() => Detect(SystemNetworkAdapterSource.Instance);

    public static VpnDetectionResult Detect(INetworkAdapterSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<NetworkAdapterInfo> adapters;
        try { adapters = source.GetAdapters(); }
        catch { return VpnDetectionResult.NotDetected; }
        if (adapters is null || adapters.Count == 0) return VpnDetectionResult.NotDetected;

        IPAddress? outbound;
        try { outbound = source.GetOutboundSourceAddress(); }
        catch { outbound = null; }

        VpnDetectionResult? best = null;
        foreach (var adapter in adapters)
        {
            if (adapter is null || !adapter.IsUp) continue;
            var marker = MarkerFor(adapter);
            if (marker is null) continue;

            var holdsDefaultRoute = outbound is not null
                && adapter.UnicastAddresses is not null
                && adapter.UnicastAddresses.Any(a => a is not null && a.Equals(outbound));
            var confidence = holdsDefaultRoute ? VpnConfidence.High : VpnConfidence.Low;
            var candidate = new VpnDetectionResult(confidence, adapter.Name, adapter.Description, marker, holdsDefaultRoute);
            if (best is null || candidate.Confidence > best.Confidence) best = candidate;
            if (best.Confidence == VpnConfidence.High) break;
        }

        return best ?? VpnDetectionResult.NotDetected;
    }

    /// <summary>The marker that made the interface a candidate, or null if it is not one.</summary>
    public static string? MarkerFor(NetworkAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var name = adapter.Name ?? string.Empty;
        var description = adapter.Description ?? string.Empty;
        if (IsExcluded(name) || IsExcluded(description)) return null;

        foreach (var marker in SubstringMarkers)
        {
            if (Contains(name, marker) || Contains(description, marker)) return marker;
        }
        foreach (var marker in TokenMarkers)
        {
            if (HasToken(name, marker) || HasToken(description, marker)) return marker;
        }
        // The type alone is enough once the non-VPN tunnels are excluded.
        return adapter.IsTunnelType ? "tunnel-type" : null;
    }

    private static bool IsExcluded(string text) => Exclusions.Any(x => Contains(text, x));

    private static bool Contains(string text, string needle)
        => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>A whole word, or a word followed by a number (utun3, wg0, tun1).</summary>
    private static bool HasToken(string text, string token)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            if (i > start)
            {
                var word = text.AsSpan(start, i - start);
                var letters = word;
                while (letters.Length > 0 && char.IsDigit(letters[^1])) letters = letters[..^1];
                if (letters.Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// The real source: <see cref="NetworkInterface"/> for the interfaces' addresses, and a UDP socket "connected" to a public address
/// to learn the outbound source address. The socket <b>sends no byte</b>; connecting on UDP is a local route choice only.
/// </summary>
public sealed class SystemNetworkAdapterSource : INetworkAdapterSource
{
    /// <summary>Addresses nothing is sent to; they are used only to ask the system about the egress route.</summary>
    private static readonly IPEndPoint ProbeV4 = new(IPAddress.Parse("192.0.2.1"), 53);        // TEST-NET-1 (RFC 5737)
    private static readonly IPEndPoint ProbeV6 = new(IPAddress.Parse("2001:db8::1"), 53);      // Documentation (RFC 3849)

    public static SystemNetworkAdapterSource Instance { get; } = new();

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var result = new List<NetworkAdapterInfo>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in interfaces)
        {
            try
            {
                var properties = nic.GetIPProperties();
                result.Add(new NetworkAdapterInfo(
                    nic.Name,
                    nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up,
                    nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp,
                    properties.UnicastAddresses.Select(u => u.Address).ToArray(),
                    properties.GatewayAddresses.Select(g => g.Address).ToArray()));
            }
            catch
            {
                // One broken interface does not stop the rest
            }
        }
        return result;
    }

    public IPAddress? GetOutboundSourceAddress() => Probe(AddressFamily.InterNetwork, ProbeV4) ?? Probe(AddressFamily.InterNetworkV6, ProbeV6);

    private static IPAddress? Probe(AddressFamily family, IPEndPoint destination)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(destination);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }
}
