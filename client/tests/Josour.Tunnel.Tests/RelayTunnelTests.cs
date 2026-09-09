using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Transport;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests;

/// <summary>
/// جلسة كاملة فوق الـ Relay (ADR-0009): بلا مرشحين مباشرين إطلاقًا، فالمسار الوحيد الممكن هو الـ Relay.
///
/// <para>ما تثبته هذه الاختبارات تحديدًا هو النقطة التي كان الكود يتركها مفتوحة: على المسار المباشر «المستمع هو
/// TLS Server»، وفوق الـ Relay لا مستمع أصلًا — الطرفان يتصلان خارجًا. لو بقيت القاعدة مشتقة من «من اتصل» لانتظر
/// كلاهما مصافحة الآخر ولتعلّقت كل جلسة. القاعدة المعتمدة في docs/protocol.md القسم 3: المضيف Server دائمًا.</para>
/// </summary>
public class RelayTunnelTests
{
    private const string GuestToken = "guest-token";
    private const string HostToken = "host-token";

    private sealed record Pair(TunnelSession Host, TunnelSession Guest, TunnelConnectResult HostResult, TunnelConnectResult GuestResult) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Guest.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static TunnelSession Build(Guid sessionId, byte[] secret, TunnelRole role, FakeRelay relay, string token, List<string> log)
    {
        var material = new SessionMaterial(sessionId, role, (byte[])secret.Clone(), DateTimeOffset.UtcNow.AddMinutes(30), false, "198.51.100.2");
        return new TunnelSession(material, new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => new StaticCandidateSource(),
            // اسم مضيف لا عنوان حرفي: هذا شكل RELAY_HOST الحقيقي، وتمرير "127.0.0.1" هنا هو ما أخفى
            // عطلًا وصل إلى الإنتاج — النقل كان يرفض الاسم قبل أن يفتح مقبسًا.
            Relay = new RelayEndpointInfo("localhost", relay.Port, token),
            HostEgress = role == TunnelRole.Host ? _ => new FakeEgress(log) : null,
            GuestProxy = role == TunnelRole.Guest ? _ => new FakeProxy(log) : null,
            Mux = new MuxOptions { PingInterval = TimeSpan.FromSeconds(2), DeadAfter = TimeSpan.FromSeconds(8) },
        });
    }

    private static async Task<Pair> ConnectOverRelayAsync(FakeRelay relay)
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        var log = new List<string>();
        var host = Build(sessionId, secret, TunnelRole.Host, relay, HostToken, log);
        var guest = Build(sessionId, secret, TunnelRole.Guest, relay, GuestToken, log);

        var hostLocal = await host.PrepareAsync(CancellationToken.None);
        var guestLocal = await guest.PrepareAsync(CancellationToken.None);
        var window = TimeSpan.FromSeconds(20);

        // مصفوفة مرشحين فارغة للطرفين: لا مسار مباشر بأي حال، فما ينجح هو الـ Relay وحده.
        var hostTask = host.ConnectAsync(new PeerEndpointInfo(guestLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);
        var guestTask = guest.ConnectAsync(new PeerEndpointInfo(hostLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);
        return new Pair(host, guest, await hostTask, await guestTask);
    }

    [Fact]
    public async Task ASessionConnectsOverTheRelayWithNoDirectPathAtAll()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        Assert.True(pair.HostResult.Connected, pair.HostResult.FailureReason);
        Assert.True(pair.GuestResult.Connected, pair.GuestResult.FailureReason);
        Assert.Equal(CandidateType.Relay, pair.HostResult.WinnerType);
        Assert.Equal(CandidateType.Relay, pair.GuestResult.WinnerType);
        Assert.Equal(TunnelState.Connected, pair.Host.State);
        Assert.Equal(TunnelState.Connected, pair.Guest.State);
    }

    [Fact]
    public async Task TlsStillNegotiatesAtLeast12OverTheRelay()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        // الـ Relay ينقل بايتات معتمة: TLS والمصادقة بين الجهازين كما في المباشر تمامًا.
        Assert.Contains(pair.HostResult.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(pair.HostResult.TlsVersion, pair.GuestResult.TlsVersion);
    }

    [Fact]
    public async Task EachPartySendsItsOwnRoleAndTokenInThePreamble()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        var received = relay.Received;
        Assert.Equal(2, received.Count);
        Assert.Single(received, p => p.Role == TunnelRole.Host && p.Token == HostToken);
        Assert.Single(received, p => p.Role == TunnelRole.Guest && p.Token == GuestToken);
        Assert.Single(received.Select(p => p.SessionId).Distinct());
    }

    [Fact]
    public async Task TheRelayTokenNeverReachesTheDiagnostics()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        // التوكن بيان حامل حتى انتهاء صلاحيته، والتشخيص يُرفع إلى الخادم في session.connect_failed.
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            var rendered = string.Join("\n", session.Diagnostics.Select(kv => $"{kv.Key}={Describe(kv.Value)}"));
            Assert.DoesNotContain(HostToken, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestToken, rendered, StringComparison.Ordinal);
            Assert.Contains("relay_endpoint", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARejectedRelayLeavesTheSessionUnconnectedRatherThanHanging()
    {
        await using var relay = new FakeRelay { ForcedStatus = RelayStatus.Unauthorized };
        await using var pair = await ConnectOverRelayAsync(relay);

        // بلا مسار مباشر ومع رفض الـ Relay لا يبقى شيء؛ المهم أن ينتهي بنتيجة لا أن يعلّق حتى المهلة الخارجية.
        Assert.False(pair.HostResult.Connected);
        Assert.False(pair.GuestResult.Connected);
    }

    private static string Describe(object? value) => value switch
    {
        null => "",
        IDictionary<string, object?> map => string.Join(",", map.Select(kv => $"{kv.Key}={Describe(kv.Value)}")),
        System.Collections.IEnumerable items and not string => string.Join(",", items.Cast<object?>().Select(Describe)),
        _ => value.ToString() ?? "",
    };
}
