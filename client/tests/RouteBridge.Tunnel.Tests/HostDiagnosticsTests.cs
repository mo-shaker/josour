using System.Net;
using RouteBridge.Core.Net;
using RouteBridge.Tunnel.Diagnostics;

namespace RouteBridge.Tunnel.Tests;

/// <summary>
/// عقد <c>hello.diagnostics</c> (docs/ws-protocol.md): ستة مفاتيح بالضبط، ولا رمي مهما فشلت المنصة.
/// وقواعد كشف الـ VPN نفسها مختبَرة في <c>RouteBridge.Core.Tests.VpnDetectorTests</c>.
/// </summary>
public class HostDiagnosticsTests
{
    private static readonly string[] FrozenKeys =
    {
        "firewall_rule_present", "firewall_profile", "vpn_adapter", "system_proxy_present", "os_build", "ipv6_global",
    };

    [Fact]
    public async Task Collect_ReturnsExactlyTheFrozenKeys_AndNeverThrows()
    {
        var diagnostics = await HostDiagnostics.CollectAsync(CancellationToken.None);
        Assert.Equal(FrozenKeys.OrderBy(k => k, StringComparer.Ordinal), diagnostics.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.IsType<bool>(diagnostics["vpn_adapter"]);
        Assert.IsType<bool>(diagnostics["ipv6_global"]);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics["os_build"] as string));
        // القيم غير القابلة للتحديد على هذه المنصة تكون null، لا استثناءً.
        Assert.True(diagnostics["system_proxy_present"] is null or bool);
        Assert.True(diagnostics["firewall_rule_present"] is null or bool);
    }

    [Fact]
    public async Task Collect_IsCancellable_WithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var diagnostics = await HostDiagnostics.CollectAsync(cts.Token);
        Assert.Equal(FrozenKeys.Length, diagnostics.Count);
    }

    [Fact]
    public void DetectVpn_UsesTheInjectedSource()
    {
        var source = new FixedAdapterSource(
            new[]
            {
                new NetworkAdapterInfo("Ethernet", "Realtek GbE", true, false, new[] { IPAddress.Parse("192.168.1.5") }, new[] { IPAddress.Parse("192.168.1.1") }),
                new NetworkAdapterInfo("wg0", "WireGuard Tunnel", true, true, new[] { IPAddress.Parse("10.6.0.2") }, Array.Empty<IPAddress>()),
            },
            IPAddress.Parse("10.6.0.2"));

        var result = HostDiagnostics.DetectVpn(source);
        Assert.Equal(VpnConfidence.High, result.Confidence);
        Assert.True(result.ShouldWarn);
        Assert.Equal("wg0", result.AdapterName);
    }

    [Fact]
    public void DetectVpn_ThrowingSource_ReturnsNotDetected()
    {
        Assert.Same(VpnDetectionResult.NotDetected, HostDiagnostics.DetectVpn(new ThrowingAdapterSource()));
    }

    [Fact]
    public void VpnAdapterPresent_MatchesDetectVpn_OnTheRealMachine()
    {
        Assert.Equal(HostDiagnostics.DetectVpn().IsVpn, HostDiagnostics.VpnAdapterPresent());
    }

    private sealed class FixedAdapterSource : INetworkAdapterSource
    {
        private readonly IReadOnlyList<NetworkAdapterInfo> _adapters;
        private readonly IPAddress? _outbound;

        public FixedAdapterSource(IReadOnlyList<NetworkAdapterInfo> adapters, IPAddress? outbound)
        {
            _adapters = adapters;
            _outbound = outbound;
        }

        public IReadOnlyList<NetworkAdapterInfo> GetAdapters() => _adapters;
        public IPAddress? GetOutboundSourceAddress() => _outbound;
    }

    private sealed class ThrowingAdapterSource : INetworkAdapterSource
    {
        public IReadOnlyList<NetworkAdapterInfo> GetAdapters() => throw new InvalidOperationException("no network stack");
        public IPAddress? GetOutboundSourceAddress() => throw new InvalidOperationException("no network stack");
    }
}
