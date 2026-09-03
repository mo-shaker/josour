using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RouteBridge.Tunnel.Candidates;

/// <summary>قراءة عناوين واجهات الجهاز. لا يرمي أبدًا؛ أخطاء التعداد تعطي نتائج فارغة.</summary>
public static class LocalNetwork
{
    public sealed record LocalAddress(IPAddress Address, NetworkInterface Interface, UnicastIPAddressInformation Info);

    /// <summary>عناوين unicast لكل الواجهات غير loopback (الواجهات العاملة فقط عند onlyUp).</summary>
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
                // واجهة واحدة معطوبة لا تمنع الباقي
            }
        }
        return result;
    }

    /// <summary>كل عناوين الجهاز (بما فيها loopback والواجهات المتوقفة) وبواباته الافتراضية، لسياسة IpRangePolicy على المضيف.</summary>
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
                // تجاهل
            }
        }
        return result;
    }

    public static bool IsLanIPv4(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    /// <summary>IPv6 عام: ليس loopback ولا link-local ولا site-local ولا ULA ولا multicast ولا v4-mapped ولا Teredo.</summary>
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

    /// <summary>ليس عنوانًا مؤقتًا (privacy/random suffix) ولا transient ولا في حالة DAD غير مفضّلة. الخصائص غير المدعومة على المنصة تُتجاهل.</summary>
    public static bool IsStableAddress(UnicastIPAddressInformation info)
    {
        // WINDOWS-ONLY: SuffixOrigin/IsTransient/DuplicateAddressDetectionState تُقرأ على Windows فقط؛ على غيره تُعتبر كل العناوين مستقرة.
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
