using System.Net;
using System.Net.Sockets;
using Josour.Core.Net;

namespace Josour.Tunnel.Candidates;

/// <summary>What direct inbound can expect from this machine's position in the network.</summary>
public enum NatReachability
{
    /// <summary>Nothing could be established. Not a synonym for "fine".</summary>
    Unknown,

    /// <summary>A local address is the address the server sees: the machine is on the internet, no NAT in the way.</summary>
    Open,

    /// <summary>Behind one NAT that answered and mapped the port. Direct has a chance.</summary>
    Mapped,

    /// <summary>Behind a NAT that cannot be opened from here. Direct will not work; the relay will.</summary>
    Blocked,
}

/// <summary>
/// The NAT verdict, as three separate facts.
/// <para>
/// <see cref="CgnatSuspected"/> alone used to carry all of it, and it lied by omission: it was computed
/// only from what a UPnP gateway reported, so with no gateway to ask it came back <c>false</c> — the same
/// value as "asked, and there is no CGNAT". Behind phone tethering, which is where CGNAT actually lives,
/// there is no gateway. The signal was silent in precisely the case it existed for.
/// </para>
/// <para>
/// So <see cref="Checked"/> now separates "we asked" from "we could not ask", the way
/// <c>vpn_holds_default_route</c> was split from <c>vpn_adapter</c> in week 6, and
/// <see cref="Reachability"/> answers the question a failed connection actually raises — could direct
/// have worked at all — which is often answerable even when CGNAT is not.
/// </para>
/// </summary>
/// <param name="CgnatSuspected">Positive evidence of carrier-grade or double NAT. Never inferred from silence.</param>
/// <param name="Checked">Whether any source could answer. When false, <see cref="CgnatSuspected"/> means "unknown", not "no".</param>
/// <param name="Reachability">What direct inbound can expect.</param>
/// <param name="Evidence">The observation the verdict rests on, for reading a diagnostics row months later.</param>
public sealed record NatAssessment(bool CgnatSuspected, bool Checked, NatReachability Reachability, string Evidence)
{
    /// <summary>The carrier-grade NAT shared address space (RFC 6598). A local address here is CGNAT with no gateway needed.</summary>
    public static readonly (IPAddress Network, int Bits) SharedAddressSpace = (IPAddress.Parse("100.64.0.0"), 10);

    /// <summary>
    /// Weighs every source that can speak: the machine's own addresses, the address the server sees, and
    /// the gateway if one answered. Pure — no I/O, so the cases that matter are the ones a test can build.
    /// </summary>
    public static NatAssessment Assess(
        IReadOnlyList<IPAddress> localAddresses,
        IPAddress? serverSeenPublicIp,
        bool gatewayFound,
        bool mappingOk,
        IPAddress? gatewayExternalIp)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);

        var external = Usable(gatewayExternalIp) ? gatewayExternalIp : null;
        var externalMatchesPublic = external is not null && serverSeenPublicIp is not null && external.Equals(serverSeenPublicIp);

        // 1. The machine holds the public address itself. Nothing is translating anything.
        foreach (var local in localAddresses)
        {
            if (local is not null && serverSeenPublicIp is not null && local.Equals(serverSeenPublicIp))
                return new NatAssessment(false, Checked: true, NatReachability.Open, "local_address_is_the_public_address");
        }

        // 2. A local address inside 100.64.0.0/10. This is the tethering case the old check slept through:
        //    it is carrier-grade NAT stated outright by the address, and no gateway is needed to see it.
        //    The exception is an ISP router that hands out 100.64/10 on its LAN side while owning the public
        //    address - one controllable NAT, not two - which the gateway itself tells us.
        var localCgnat = localAddresses.Any(a => a is not null && InSharedAddressSpace(a));
        if (localCgnat && !externalMatchesPublic)
        {
            // A port mapped below carrier NAT is a mapping to nowhere: the translation above it is not ours.
            return new NatAssessment(true, Checked: true, NatReachability.Blocked, "local_address_in_100.64.0.0/10");
        }

        if (external is not null)
        {
            // 3. The gateway's own outside address is private: there is another NAT above it.
            if (IpRangePolicy.IsBlockedRange(external))
                return new NatAssessment(true, Checked: true, NatReachability.Blocked, "gateway_external_address_is_private");

            // 4. The gateway's outside address is not what the server sees: something translates above it.
            if (serverSeenPublicIp is not null && !external.Equals(serverSeenPublicIp))
                return new NatAssessment(true, Checked: true, NatReachability.Blocked, "gateway_external_address_differs_from_public");

            // 5. One NAT, and it answered.
            return new NatAssessment(
                false, Checked: true,
                mappingOk ? NatReachability.Mapped : NatReachability.Blocked,
                mappingOk ? "single_nat_mapped" : "single_nat_but_mapping_failed");
        }

        // 6. Nothing could be asked. Say so - but not everything is unknown: if the server sees an address
        //    that this machine does not hold, inbound is blocked whatever the shape of the NAT above.
        var reachability = serverSeenPublicIp is not null ? NatReachability.Blocked : NatReachability.Unknown;
        var evidence = gatewayFound ? "gateway_found_but_reported_no_external_address" : "no_gateway_answered";
        return new NatAssessment(false, Checked: false, reachability, evidence);
    }

    private static bool Usable(IPAddress? address)
        => address is not null
           && !address.Equals(IPAddress.Any)
           && !address.Equals(IPAddress.IPv6Any)
           && !IPAddress.IsLoopback(address);

    private static bool InSharedAddressSpace(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        // 100.64.0.0/10: first octet 100, and the top two bits of the second octet are 01 (64..127).
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }
}
