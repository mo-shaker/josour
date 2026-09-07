using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Josour.Core.Net;

/// <summary>ثقة كشف الـ VPN. انظر <see cref="VpnDetector"/> للقواعد.</summary>
public enum VpnConfidence
{
    /// <summary>لا واجهة VPN عاملة.</summary>
    None = 0,

    /// <summary>واجهة VPN موجودة وعاملة لكنها لا تحمل مسار الخروج: الحركة على الأرجح لا تمر بها.</summary>
    Low = 1,

    /// <summary>واجهة VPN تحمل مسار الخروج الافتراضي: الحركة الخارجة تمر بها فعلًا.</summary>
    High = 2,
}

/// <summary>وصف واجهة شبكة كما تراه أداة الكشف. قابل للتلفيق في الاختبارات.</summary>
/// <param name="Name">اسم الواجهة (utun3، wg0، Ethernet 2).</param>
/// <param name="Description">وصف المنتج (WireGuard Tunnel، TAP-Windows Adapter V9).</param>
/// <param name="IsUp">هل الواجهة عاملة.</param>
/// <param name="IsTunnelType">هل نوعها Tunnel/Ppp حسب النظام.</param>
/// <param name="UnicastAddresses">عناوين الواجهة.</param>
/// <param name="GatewayAddresses">بوابات الواجهة (وجودها شرط ضروري لحمل مسار افتراضي، وليس كافيًا).</param>
public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    bool IsUp,
    bool IsTunnelType,
    IReadOnlyList<IPAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> GatewayAddresses);

/// <summary>مصدر الواجهات ومسار الخروج. يُحقن في الاختبارات.</summary>
public interface INetworkAdapterSource
{
    IReadOnlyList<NetworkAdapterInfo> GetAdapters();

    /// <summary>
    /// عنوان المصدر الذي يختاره النظام للخروج إلى الإنترنت، أو null إن تعذّر تحديده.
    /// المطابقة معه هي كيف نعرف أي واجهة تحمل المسار الافتراضي بلا قراءة جدول التوجيه.
    /// </summary>
    IPAddress? GetOutboundSourceAddress();
}

/// <summary>نتيجة الكشف: هل هناك VPN، وأي واجهة، وبأي ثقة، ولماذا.</summary>
public sealed record VpnDetectionResult(
    VpnConfidence Confidence,
    string? AdapterName,
    string? AdapterDescription,
    string? Marker,
    bool HoldsDefaultRoute)
{
    public static VpnDetectionResult NotDetected { get; } = new(VpnConfidence.None, null, null, null, false);

    public bool IsVpn => Confidence != VpnConfidence.None;

    /// <summary>هل يجب تحذير المستخدم بأن عنوان الخروج سيكون عنوان الـ VPN؟ (الثقة العالية فقط.)</summary>
    public bool ShouldWarn => Confidence == VpnConfidence.High;

    /// <summary>سطر تشخيصي قصير للسجل ولـ <c>hello.diagnostics</c>.</summary>
    public string Describe() => Confidence == VpnConfidence.None
        ? "none"
        : $"{Confidence.ToString().ToLowerInvariant()}:{AdapterName}" + (Marker is null ? string.Empty : $" ({Marker})");
}

/// <summary>
/// كشف واجهات الـ VPN على المضيف (الخطة 8.5 وجدول المخاطر: «المضيف على VPN: IP الخروج هو IP الـ VPN»).
///
/// <para><b>القواعد:</b> الواجهة مرشّحة إن كانت <b>عاملة</b> و(نوعها Tunnel/Ppp أو طابق اسمها/وصفها أحد
/// <see cref="Markers"/>) ولم تطابق أحد <see cref="Exclusions"/>. المرشّحة التي تملك <b>عنوان المصدر الخارج</b>
/// (أي التي يخرج منها فعلًا) ثقتها <see cref="VpnConfidence.High"/>؛ ومجرد وجودها ثقته <see cref="VpnConfidence.Low"/>.</para>
///
/// <para><b>لماذا الاستثناءات:</b> Windows يصنّف Teredo وISATAP و6to4 على أنها <c>NetworkInterfaceType.Tunnel</c>
/// وهي ليست VPN؛ وmacOS يفتح واجهات <c>utun</c> لـ Handoff وiCloud Private Relay بلا أي VPN. بدون هذه
/// الاستثناءات يصير الكشف تحذيرًا كاذبًا دائمًا على أجهزة عادية.</para>
///
/// <para><b>لماذا الرموز القصيرة تُطابَق ككلمات:</b> «tun» و«tap» و«wg» مقاطع شائعة داخل كلمات أخرى
/// (Fortune، Adaptap)، فتُطابَق على حدود الكلمات فقط؛ والرموز الطويلة تُطابَق كسلسلة فرعية.</para>
/// </summary>
public static class VpnDetector
{
    /// <summary>رموز تُطابَق كسلسلة فرعية (طويلة بما يكفي ألا تصطدم بكلمات أخرى).</summary>
    private static readonly string[] SubstringMarkers =
    {
        "vpn", "wireguard", "tailscale", "openvpn", "zerotier", "anyconnect", "globalprotect",
        "forticlient", "fortissl", "pulse secure", "sonicwall", "check point", "checkpoint",
        "softether", "mullvad", "nordlynx", "proton", "zscaler", "netbird", "twingate",
        "wintun", "tunnelbear", "hamachi", "ipsec", "l2tp", "pptp", "sstp", "cloudflare warp",
    };

    /// <summary>رموز تُطابَق ككلمة كاملة أو ببادئة رقمية (utun3، wg0، tun0، tap-windows).</summary>
    private static readonly string[] TokenMarkers = { "tun", "utun", "tap", "wg", "ppp", "gpd", "nordvpn" };

    /// <summary>واجهات نفقية ليست VPN.</summary>
    private static readonly string[] Exclusions =
    {
        "teredo", "isatap", "6to4", "loopback", "pseudo-interface", "bluetooth", "ipv6 helper",
    };

    /// <summary>الرموز المعروضة في التوثيق والاختبارات.</summary>
    public static IReadOnlyList<string> Markers { get; } = SubstringMarkers.Concat(TokenMarkers).ToArray();

    /// <summary>كشف باستخدام واجهات النظام الحقيقية.</summary>
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

    /// <summary>الرمز الذي جعل الواجهة مرشّحة، أو null إن لم تكن.</summary>
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
        // النوع وحده يكفي بعد استبعاد الأنفاق غير الـ VPN.
        return adapter.IsTunnelType ? "tunnel-type" : null;
    }

    private static bool IsExcluded(string text) => Exclusions.Any(x => Contains(text, x));

    private static bool Contains(string text, string needle)
        => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>كلمة كاملة، أو كلمة متبوعة برقم (utun3، wg0، tun1).</summary>
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
/// المصدر الحقيقي: <see cref="NetworkInterface"/> لعناوين الواجهات، ومقبس UDP «متصل» بعنوان عام
/// لمعرفة عنوان المصدر الخارج. المقبس <b>لا يرسل أي بايت</b>؛ الاتصال على UDP اختيارُ مسارٍ محلي فقط.
/// </summary>
public sealed class SystemNetworkAdapterSource : INetworkAdapterSource
{
    /// <summary>عناوين لا يُرسل إليها شيء؛ تُستعمل لسؤال النظام عن مسار الخروج فقط.</summary>
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
                // واجهة واحدة معطوبة لا تمنع الباقي
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
