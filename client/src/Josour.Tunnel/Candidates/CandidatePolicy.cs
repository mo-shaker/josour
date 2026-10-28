using System.Net;
using System.Net.Sockets;

namespace Josour.Tunnel.Candidates;

/// <summary>
/// Which of the other side's candidates we dial. They arrive in <c>session.peer_endpoint</c>, that is, from the peer through the server,
/// so a hostile peer could otherwise make this machine open TCP connections (and send a TLS ClientHello) to anything it names:
/// this machine's own loopback services, a multicast or broadcast address, or the unspecified address (which the operating system
/// reads as "this host"). LAN addresses stay dialable — a peer on the same network legitimately offers them.
/// </summary>
public static class CandidatePolicy
{
    /// <summary>
    /// null when the candidate may be dialled, otherwise a short reason for the diagnostics.
    /// <paramref name="allowLoopback"/> is for in-process setups only (both sides on 127.0.0.1); see <see cref="SymmetricConnector"/>.
    /// </summary>
    public static string? RefusalReason(string ip, int port, bool allowLoopback)
    {
        if (!IPAddress.TryParse(ip, out var address)) return "not_an_ip";
        if (port is < 1 or > 65535) return "bad_port";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return "unspecified";                       // 0.0.0.0/8: "this network"
            if (b[0] == 127) return allowLoopback ? null : "loopback";
            if (b[0] >= 224 && b[0] <= 239) return "multicast";
            if (b[0] >= 240) return "broadcast";                        // 240.0.0.0/4 reserved, 255.255.255.255 limited broadcast
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any)) return "unspecified";
            if (address.Equals(IPAddress.IPv6Loopback)) return allowLoopback ? null : "loopback";
            if (address.IsIPv6Multicast) return "multicast";
            return null;
        }

        return "unsupported_family";
    }
}
