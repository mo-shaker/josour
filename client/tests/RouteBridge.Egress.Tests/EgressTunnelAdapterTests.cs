using System.Net;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;
using RouteBridge.Tunnel;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Egress.Tests;

/// <summary>
/// الجسر بين <see cref="TunnelSession"/> وسياسة الخروج: بناء السياسة من سياق الجلسة وربطها بـ acceptor الـ Mux.
/// </summary>
public class EgressTunnelAdapterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static TunnelEgressContext Context(
        IMuxAcceptor acceptor,
        string[] entries,
        IHostResolver resolver,
        int[]? allowedPorts = null,
        IReadOnlyList<IPAddress>? blockedLocals = null,
        int? maxConcurrentStreams = null)
        => maxConcurrentStreams is int max
            ? new(AllowlistMatcher.Parse(1, entries), allowedPorts ?? new[] { 80, 443 }, resolver, blockedLocals ?? Array.Empty<IPAddress>(), acceptor, max)
            : new(AllowlistMatcher.Parse(1, entries), allowedPorts ?? new[] { 80, 443 }, resolver, blockedLocals ?? Array.Empty<IPAddress>(), acceptor);

    [Fact]
    public async Task Attach_WiresTheAcceptor_AndAllowedOpenCountsBytesAndCollectsTheDomain()
    {
        using var origin = new LocalOrigin();
        var acceptor = new FakeAcceptor();
        var resolver = new StubResolver().Map("site.test", "127.0.0.1");
        await using var adapter = EgressTunnelAdapter.Create(Context(acceptor, new[] { $"site.test:{origin.Port}" }, resolver), Policies.AllowLoopbackOnly);
        adapter.Attach(acceptor);
        Assert.NotNull(acceptor.OpenRequested);

        var accept = origin.AcceptAsync();
        var decision = await acceptor.OpenRequested!(new MuxOpenRequest("Site.Test.", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.True(decision.IsOk);
        using var accepted = await accept.WaitAsync(Timeout);

        await using (var target = decision.Target!)
        {
            await target.WriteAsync(Encoding.ASCII.GetBytes("hello"));
            await target.FlushAsync();
            var buffer = new byte[5];
            await accepted.ReceiveAsync(buffer).WaitAsync(Timeout);
            Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
            await accepted.SendAsync(Encoding.ASCII.GetBytes("hi!")).WaitAsync(Timeout);
            var back = new byte[3];
            await target.ReadExactlyAsync(back).AsTask().WaitAsync(Timeout);
        }

        Assert.Equal(5, adapter.BytesUp);
        Assert.Equal(3, adapter.BytesDown);
        Assert.Equal(new[] { "site.test" }, adapter.Domains);
    }

    [Fact]
    public async Task Create_UsesAllowedPortsFromTheContext()
    {
        var acceptor = new FakeAcceptor();
        var resolver = new StubResolver().Map("site.test", "127.0.0.1");
        await using var adapter = EgressTunnelAdapter.Create(Context(acceptor, new[] { "site.test" }, resolver, allowedPorts: new[] { 80 }), Policies.AllowLoopbackOnly);
        adapter.Attach(acceptor);

        var decision = await acceptor.OpenRequested!(new MuxOpenRequest("site.test", 8080), CancellationToken.None).WaitAsync(Timeout);

        Assert.False(decision.IsOk);
        Assert.Equal(OpenFailReason.PortNotAllowed, decision.Reason);
        Assert.Empty(adapter.Domains);
        Assert.Equal(0, resolver.Calls); // الرفض قبل DNS
    }

    [Fact]
    public async Task Create_BlocksTheHostsOwnAddresses_FromTheContext()
    {
        var acceptor = new FakeAcceptor();
        var ours = IPAddress.Parse("93.184.216.34"); // عنوان عام لا تحظره النطاقات وحدها
        var resolver = new StubResolver().Map("mirror.test", ours.ToString());
        await using var adapter = EgressTunnelAdapter.Create(Context(acceptor, new[] { "mirror.test" }, resolver, blockedLocals: new[] { ours }));
        adapter.Attach(acceptor);

        var decision = await acceptor.OpenRequested!(new MuxOpenRequest("mirror.test", 443), CancellationToken.None).WaitAsync(Timeout);

        Assert.False(decision.IsOk);
        Assert.Equal(OpenFailReason.PrivateIp, decision.Reason);
        Assert.Empty(adapter.Domains);
    }

    [Fact]
    public async Task Create_RejectsUnlistedHosts_AndExposesTheHandler()
    {
        var acceptor = new FakeAcceptor();
        var adapter = (EgressTunnelAdapter)EgressTunnelAdapter.Create(Context(acceptor, new[] { "site.test" }, new StubResolver()));
        adapter.Attach(acceptor);

        var decision = await acceptor.OpenRequested!(new MuxOpenRequest("other.test", 443), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(OpenFailReason.NotAllowed, decision.Reason);
        Assert.Equal(1, adapter.Handler.OpensFailed);
        Assert.Equal(0, adapter.Handler.OpensOk);
        await adapter.DisposeAsync();
        await adapter.DisposeAsync(); // آمن مرتين
    }

    [Fact]
    public async Task Create_HonoursACustomLimiter()
    {
        using var origin = new LocalOrigin();
        var acceptor = new FakeAcceptor();
        var resolver = new StubResolver().Map("site.test", "127.0.0.1");
        await using var adapter = EgressTunnelAdapter.Create(
            Context(acceptor, new[] { $"site.test:{origin.Port}" }, resolver),
            Policies.AllowLoopbackOnly,
            new StreamLimiter(maxConcurrent: 1, maxOpensPerSecond: 1));
        adapter.Attach(acceptor);

        var accept = origin.AcceptAsync();
        var first = await acceptor.OpenRequested!(new MuxOpenRequest("site.test", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.True(first.IsOk);
        using var accepted = await accept.WaitAsync(Timeout);

        var second = await acceptor.OpenRequested!(new MuxOpenRequest("site.test", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.Limit, second.Reason);
        await first.Target!.DisposeAsync();
    }

    /// <summary>
    /// حد الـ streams المتزامنة يأتي من شريحة النافذة التي اشتقتها الجلسة (docs/protocol.md القسم 5:
    /// 256 عند 1 MiB، 128 عند 2 MiB، 64 عند 4 MiB) ولم يعد 256 مثبتًا. حد الـ 50 فتحة/ثانية لا يتغير.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(128)]
    [InlineData(64)]
    public void Create_TakesTheStreamLimitFromTheSessionContext(int maxConcurrent)
    {
        var acceptor = new FakeAcceptor();
        var adapter = (EgressTunnelAdapter)EgressTunnelAdapter.Create(
            Context(acceptor, new[] { "site.test" }, new StubResolver(), maxConcurrentStreams: maxConcurrent),
            Policies.AllowLoopbackOnly);

        Assert.Equal(maxConcurrent, adapter.Handler.Limiter.MaxConcurrent);
        Assert.Equal(StreamLimiter.DefaultMaxOpensPerSecond, adapter.Handler.Limiter.MaxOpensPerSecond);
        Assert.Equal(50, adapter.Handler.Limiter.MaxOpensPerSecond);
    }

    [Fact]
    public void Create_WithoutADerivedLimit_KeepsTheContractDefault()
    {
        var acceptor = new FakeAcceptor();
        var adapter = (EgressTunnelAdapter)EgressTunnelAdapter.Create(Context(acceptor, new[] { "site.test" }, new StubResolver()));

        Assert.Equal(256, adapter.Handler.Limiter.MaxConcurrent);
        Assert.Equal(50, adapter.Handler.Limiter.MaxOpensPerSecond);
    }
}

/// <summary>acceptor وهمي: يحتفظ بالمعالج الذي يسجّله المحوّل ليستدعيه الاختبار مباشرة.</summary>
internal sealed class FakeAcceptor : IMuxAcceptor
{
    public Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
    public MuxStats Stats => default;
    public Task Completion => Task.CompletedTask;
    public GoAwayReason? RemoteGoAway => null;
    public Task<TimeSpan> PingAsync(CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
    public Task CloseAsync(GoAwayReason reason) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
