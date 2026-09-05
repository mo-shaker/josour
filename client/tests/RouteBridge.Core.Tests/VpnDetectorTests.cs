using System.Net;
using RouteBridge.Core.Net;

namespace RouteBridge.Core.Tests;

/// <summary>مصدر واجهات ملفَّق: يجعل قواعد <see cref="VpnDetector"/> قابلة للاختبار بلا شبكة حقيقية.</summary>
internal sealed class FakeAdapterSource : INetworkAdapterSource
{
    private readonly List<NetworkAdapterInfo> _adapters = new();

    public IPAddress? Outbound { get; set; }
    public bool ThrowOnEnumerate { get; set; }
    public bool ThrowOnOutbound { get; set; }

    public FakeAdapterSource Add(string name, string description, bool up = true, bool tunnelType = false, string? address = null, string? gateway = null)
    {
        _adapters.Add(new NetworkAdapterInfo(
            name,
            description,
            up,
            tunnelType,
            address is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(address) },
            gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) }));
        return this;
    }

    public FakeAdapterSource Outbounds(string address)
    {
        Outbound = IPAddress.Parse(address);
        return this;
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
        => ThrowOnEnumerate ? throw new InvalidOperationException("enumeration failed") : _adapters;

    public IPAddress? GetOutboundSourceAddress()
        => ThrowOnOutbound ? throw new InvalidOperationException("probe failed") : Outbound;
}

public class VpnDetectorTests
{
    private static FakeAdapterSource Ordinary() => new FakeAdapterSource()
        .Add("Ethernet", "Realtek PCIe GbE Family Controller", address: "192.168.1.20", gateway: "192.168.1.1")
        .Add("Wi-Fi", "Intel Wi-Fi 6 AX201 160MHz", address: "192.168.1.21", gateway: "192.168.1.1")
        .Outbounds("192.168.1.20");

    [Fact]
    public void OrdinaryMachine_DetectsNothing()
    {
        var result = VpnDetector.Detect(Ordinary());
        Assert.Equal(VpnConfidence.None, result.Confidence);
        Assert.False(result.IsVpn);
        Assert.False(result.ShouldWarn);
        Assert.Equal("none", result.Describe());
    }

    [Fact]
    public void VpnHoldingTheDefaultRoute_IsHighConfidence()
    {
        var source = Ordinary().Add("utun4", "WireGuard Tunnel", tunnelType: true, address: "10.7.0.2");
        source.Outbound = IPAddress.Parse("10.7.0.2");

        var result = VpnDetector.Detect(source);
        Assert.Equal(VpnConfidence.High, result.Confidence);
        Assert.True(result.ShouldWarn);
        Assert.True(result.HoldsDefaultRoute);
        Assert.Equal("utun4", result.AdapterName);
        Assert.Equal("wireguard", result.Marker);
    }

    [Fact]
    public void VpnThatMerelyExists_IsLowConfidence()
    {
        // الواجهة عاملة لكن الخروج من بطاقة الشبكة العادية: لا تحذير.
        var source = Ordinary().Add("utun4", "WireGuard Tunnel", tunnelType: true, address: "10.7.0.2");

        var result = VpnDetector.Detect(source);
        Assert.Equal(VpnConfidence.Low, result.Confidence);
        Assert.True(result.IsVpn);
        Assert.False(result.ShouldWarn);
        Assert.False(result.HoldsDefaultRoute);
    }

    [Fact]
    public void DownAdapter_IsIgnored()
    {
        var source = Ordinary().Add("utun4", "WireGuard Tunnel", up: false, tunnelType: true, address: "10.7.0.2");
        source.Outbound = IPAddress.Parse("10.7.0.2");
        Assert.Equal(VpnConfidence.None, VpnDetector.Detect(source).Confidence);
    }

    [Theory]
    [InlineData("utun3", "")]
    [InlineData("wg0", "")]
    [InlineData("tun0", "")]
    [InlineData("ppp0", "")]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9")]
    [InlineData("Local Area Connection", "OpenVPN Wintun")]
    [InlineData("Tailscale", "Tailscale Tunnel")]
    [InlineData("Ethernet 4", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter")]
    [InlineData("PANGP", "PANGP Virtual Ethernet Adapter (GlobalProtect)")]
    [InlineData("Ethernet 5", "FortiClient Virtual Ethernet Adapter")]
    [InlineData("NordLynx", "NordLynx Tunnel")]
    [InlineData("Ethernet 6", "Mullvad Tunnel")]
    [InlineData("Ethernet 7", "ZeroTier Virtual Port")]
    [InlineData("Ethernet 8", "Juniper Networks Virtual Adapter VPN")]
    public void KnownVpnAdapters_AreRecognised(string name, string description)
    {
        var source = Ordinary().Add(name, description, address: "10.9.0.2");
        var result = VpnDetector.Detect(source);
        Assert.True(result.IsVpn, $"{name} / {description} was not recognised");
        Assert.Equal(name, result.AdapterName);
    }

    [Theory]
    // أنفاق النظام التي ليست VPN: Windows يصنّفها NetworkInterfaceType.Tunnel وهي تحمل عناوين وتخرج منها حركة.
    // بدون هذه الاستثناءات يصير التحذير كاذبًا ودائمًا على أجهزة Windows عادية.
    [InlineData("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface")]
    [InlineData("isatap.{GUID}", "Microsoft ISATAP Adapter")]
    [InlineData("6to4 Adapter", "Microsoft 6to4 Adapter")]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)")]
    public void SystemPseudoTunnels_AreExcluded(string name, string description)
    {
        var source = Ordinary().Add(name, description, tunnelType: true, address: "10.9.0.2");
        source.Outbound = IPAddress.Parse("10.9.0.2");
        Assert.Equal(VpnConfidence.None, VpnDetector.Detect(source).Confidence);
    }

    [Theory]
    // بطاقات عادية تحوي المقاطع القصيرة (tun/tap/wg) داخل كلمات أخرى: لا تُطابَق إلا على حدود الكلمات.
    [InlineData("Fortune Adapter", "Fortune Networks Gigabit Adapter")]
    [InlineData("Ethernet 9", "Realtek Adaptap Bridge Miniport")]
    [InlineData("Ethernet 10", "Broadcom NetXtreme Gigabit Ethernet")]
    [InlineData("Wi-Fi 2", "Qualcomm Atheros QCA61x4A Wireless Network Adapter")]
    public void NameLookalikes_AreNotFlagged(string name, string description)
    {
        var source = Ordinary().Add(name, description, address: "10.9.0.2");
        source.Outbound = IPAddress.Parse("10.9.0.2");
        Assert.Equal(VpnConfidence.None, VpnDetector.Detect(source).Confidence);
    }

    [Fact]
    public void UnknownTunnelType_StillCounts()
    {
        // نوع Tunnel بلا رمز معروف وبلا استثناء: VPN بمُعرِّف "tunnel-type".
        var source = Ordinary().Add("Ethernet 12", "Contoso Secure Access Adapter", tunnelType: true, address: "10.9.0.2");
        var result = VpnDetector.Detect(source);
        Assert.True(result.IsVpn);
        Assert.Equal("tunnel-type", result.Marker);
    }

    [Fact]
    public void HighConfidenceWins_OverAnotherLowConfidenceAdapter()
    {
        var source = Ordinary()
            .Add("utun1", "utun", address: "10.1.0.2")                      // موجودة فقط
            .Add("wg0", "WireGuard Tunnel", address: "10.7.0.2");           // تحمل الخروج
        source.Outbound = IPAddress.Parse("10.7.0.2");

        var result = VpnDetector.Detect(source);
        Assert.Equal(VpnConfidence.High, result.Confidence);
        Assert.Equal("wg0", result.AdapterName);
        Assert.StartsWith("high:wg0", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOutboundAddress_DegradesToLow_NotHigh()
    {
        // تعذّر معرفة مسار الخروج (مقبس الفحص فشل): نحذّر بثقة منخفضة، لا نزعم اليقين.
        var source = Ordinary().Add("wg0", "WireGuard Tunnel", address: "10.7.0.2");
        source.Outbound = null;
        Assert.Equal(VpnConfidence.Low, VpnDetector.Detect(source).Confidence);
    }

    [Fact]
    public void EnumerationFailure_IsNotAnError()
    {
        Assert.Equal(VpnConfidence.None, VpnDetector.Detect(new FakeAdapterSource { ThrowOnEnumerate = true }).Confidence);
    }

    [Fact]
    public void OutboundProbeFailure_IsNotAnError()
    {
        var source = Ordinary().Add("wg0", "WireGuard Tunnel", address: "10.7.0.2");
        source.ThrowOnOutbound = true;
        Assert.Equal(VpnConfidence.Low, VpnDetector.Detect(source).Confidence);
    }

    [Fact]
    public void EmptyAdapterList_DetectsNothing()
    {
        Assert.Same(VpnDetectionResult.NotDetected, VpnDetector.Detect(new FakeAdapterSource()));
    }

    [Fact]
    public void SystemSource_NeverThrows()
    {
        // يُشغَّل على الجهاز الحقيقي: القيمة تختلف بالبيئة، لكن الاستدعاء لا يرمي ولا يعلّق.
        var adapters = SystemNetworkAdapterSource.Instance.GetAdapters();
        Assert.NotNull(adapters);
        var result = VpnDetector.Detect(SystemNetworkAdapterSource.Instance);
        Assert.NotNull(result);
        Assert.NotNull(result.Describe());
    }
}
