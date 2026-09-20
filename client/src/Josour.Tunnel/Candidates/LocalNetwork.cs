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
