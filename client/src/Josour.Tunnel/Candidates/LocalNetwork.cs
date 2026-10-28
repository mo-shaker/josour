using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Josour.Tunnel.Candidates;

/// <summary>Reading the machine's interface addresses. It never throws; enumeration errors give empty results.</summary>
public static class LocalNetwork
{
    public sealed record LocalAddress(IPAddress Address, NetworkInterface Interface, UnicastIPAddressInformation Info);

    /// <summary>The unicast addresses of every non-loopback interface (the interfaces that are up only, when onlyUp).</summary>
    public static IReadOnlyList<LocalAddress> GetUnicastAddresses(bool onlyUp = true)
    {
        var result = new List<LocalAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in interfaces)
        {
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (onlyUp && nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    result.Add(new LocalAddress(unicast.Address, nic, unicast));
            }
            catch
            {
                // One broken interface does not stop the rest
            }
        }
        return result;
    }

    /// <summary>Every address of the machine (loopback and down interfaces included) and its default gateways, for IpRangePolicy on the host.</summary>
    public static IReadOnlyList<IPAddress> GetPolicyLocalAddresses()
    {
        var result = new List<IPAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in interfaces)
        {
            try
            {
                var props = nic.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses) result.Add(unicast.Address);
                foreach (var gateway in props.GatewayAddresses) result.Add(gateway.Address);
            }
            catch
            {
                // ignore
            }
        }
        return result;
    }

    /// <summary>
    /// The host's on-link networks to block in IpRangePolicy (docs/protocol.md section 6 rule 6), so the guest cannot reach the host's
    /// LAN neighbours — its router, a NAS, a printer — through their global IPv6 addresses (or through an internal network numbered from
    /// public IPv4 space). Down interfaces are included, as in <see cref="GetPolicyLocalAddresses"/>. An interface whose prefix length the
    /// platform cannot report contributes nothing here, which leaves its exact addresses (the previous behaviour) as the only guard.
    /// </summary>
    public static IReadOnlyList<IPNetwork> GetPolicyLocalNetworks()
    {
        var result = new List<IPNetwork>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in interfaces)
        {
            UnicastIPAddressInformationCollection unicasts;
            try { unicasts = nic.GetIPProperties().UnicastAddresses; }
            catch { continue; }
            foreach (var unicast in unicasts)
            {
                int prefixLength;
                try { prefixLength = unicast.PrefixLength; }
                catch { continue; } // PlatformNotSupportedException and friends: fall back to the exact address
                if (PolicyNetworkFor(unicast.Address, prefixLength) is { } network && !result.Contains(network)) result.Add(network);
            }
        }
        return result;
    }

    /// <summary>The narrowest prefix length accepted for an on-link IPv6 network; anything wider would be an ISP's or a site's allocation.</summary>
    public const int MinPolicyPrefixV6 = 48;

    /// <summary>
    /// IPv4 on-link subnets are blocked only when narrower than a /16: a wider one may be a university's or a company's whole public block,
    /// which holds ordinary public sites, and the private ranges a /16 or wider could cover are already in the fixed table.
    /// </summary>
    public const int MinPolicyPrefixV4 = 17;

    /// <summary>
    /// The network to block for one interface address, or null when it adds nothing or would be unsafe:
    /// <list type="bullet">
    ///   <item>IPv6: global unicast only (2000::/3, not 6to4 or Teredo, which are unwrapped instead), with a prefix of /48 to /128.</item>
    ///   <item>IPv4: a subnet narrower than /16 (/17 to /31). A /32 is the exact address, which <see cref="GetPolicyLocalAddresses"/> covers;
    ///     the host's public address as the server sees it stays an exact /32 because it is not an interface address.</item>
    /// </list>
    /// </summary>
    public static IPNetwork? PolicyNetworkFor(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (prefixLength is < MinPolicyPrefixV6 or > 128) return null;
            if (!IsGlobalUnicastV6(address)) return null;
            return Mask(address, prefixLength);
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (prefixLength is < MinPolicyPrefixV4 or > 31) return null;
            return Mask(address, prefixLength);
        }
        return null;
    }

    private static bool IsGlobalUnicastV6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if ((b[0] & 0xE0) != 0x20) return false;                          // 2000::/3
        if (b[0] == 0x20 && b[1] == 0x02) return false;                   // 6to4: unwrapped by IpRangePolicy
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false; // Teredo: likewise
        return true;
    }

    private static IPNetwork Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsHere = Math.Clamp(prefixLength - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsHere));
        }
        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }

    public static bool IsLanIPv4(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    /// <summary>Public IPv6: not loopback, not link-local, not site-local, not ULA, not multicast, not v4-mapped and not Teredo.</summary>
    public static bool IsGlobalIPv6(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6
           && !IPAddress.IsLoopback(address)
           && !address.Equals(IPAddress.IPv6Any)
           && !address.IsIPv6LinkLocal
           && !address.IsIPv6SiteLocal
           && !address.IsIPv6UniqueLocal
           && !address.IsIPv6Multicast
           && !address.IsIPv4MappedToIPv6
           && !address.IsIPv6Teredo;

    /// <summary>Not a temporary address (a privacy/random suffix), not transient, and not in a non-preferred DAD state. Properties the platform does not support are ignored.</summary>
    public static bool IsStableAddress(UnicastIPAddressInformation info)
    {
        // WINDOWS-ONLY: SuffixOrigin/IsTransient/DuplicateAddressDetectionState are read on Windows only; elsewhere every address is treated as stable.
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (info.SuffixOrigin == SuffixOrigin.Random) return false;
            if (info.IsTransient) return false;
            if (info.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Deprecated
                or DuplicateAddressDetectionState.Duplicate
                or DuplicateAddressDetectionState.Invalid
                or DuplicateAddressDetectionState.Tentative) return false;
        }
        catch (PlatformNotSupportedException) { }
        return true;
    }

    public static bool HasGlobalIPv6()
    {
        foreach (var la in GetUnicastAddresses())
            if (IsGlobalIPv6(la.Address) && IsStableAddress(la.Info)) return true;
        return false;
    }
}
