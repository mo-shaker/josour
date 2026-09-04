using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Egress.Tests;

public class OpenHandlerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (OpenHandler Handler, LocalOrigin Origin) Create(StreamLimiter? limiter = null)
    {
        var origin = new LocalOrigin();
        var allowlist = AllowlistMatcher.Parse(1, new[] { $"site.test:{origin.Port}" });
        var policy = new EgressPolicy(allowlist, new EgressPolicyOptions
        {
            Resolver = new StubResolver().Map("site.test", "127.0.0.1"),
            AddressBlocker = Policies.AllowLoopbackOnly,
        });
        return (new OpenHandler(policy, limiter), origin);
    }

    [Fact]
    public async Task AllowedOpen_ReturnsCountedStream_CollectsDomain_ReleasesLeaseOnDispose()
    {
        var (handler, origin) = Create();
        using var _ = origin;
        var accept = origin.AcceptAsync();

        var decision = await handler.HandleAsync(new MuxOpenRequest("Site.Test", origin.Port), CancellationToken.None).WaitAsync(Timeout);

        Assert.True(decision.IsOk, decision.Reason?.ToString());
        Assert.Contains("site.test", handler.Domains.Snapshot());
        Assert.Equal(1, handler.Limiter.Active);
        Assert.Equal(1, handler.OpensOk);
        using var far = await accept.WaitAsync(Timeout);

        var stream = decision.Target!;
        await stream.WriteAsync(Encoding.ASCII.GetBytes("request"));
        var buffer = new byte[16];
        var n = await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("request", Encoding.ASCII.GetString(buffer, 0, n));
        await far.SendAsync(Encoding.ASCII.GetBytes("reply!"), SocketFlags.None);
        var reply = new byte[6];
        await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(Timeout);
        Assert.Equal("reply!", Encoding.ASCII.GetString(reply));

        Assert.Equal(7, handler.Counter.Up);
        Assert.Equal(6, handler.Counter.Down);
        await ((IHalfClosable)stream).CompleteWritingAsync(CancellationToken.None);
        Assert.Equal(0, await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout));

        await stream.DisposeAsync();
        Assert.Equal(0, handler.Limiter.Active);
    }

    [Theory]
    [InlineData("10.1.2.3", OpenFailReason.IpLiteral)]
    [InlineData("other.test", OpenFailReason.NotAllowed)]
    [InlineData("unresolvable.site.test", OpenFailReason.DnsFailed)]
    public async Task PolicyFailures_MapToReason_WithoutHoldingLease(string host, OpenFailReason expected)
    {
        var (handler, origin) = Create();
        using var _ = origin;
        var decision = await handler.HandleAsync(new MuxOpenRequest(host, origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(expected, decision.Reason);
        Assert.Equal(0, handler.Limiter.Active);
        Assert.Equal(1, handler.OpensFailed);
        Assert.Empty(handler.Domains.Snapshot());
    }

    [Fact]
    public async Task PortNotAllowed_Maps()
    {
        var (handler, origin) = Create();
        using var _ = origin;
        var decision = await handler.HandleAsync(new MuxOpenRequest("site.test", origin.Port + 1), CancellationToken.None);
        Assert.Equal(OpenFailReason.PortNotAllowed, decision.Reason);
    }

    [Fact]
    public async Task ConcurrentLimit_ReturnsLimit_UntilStreamDisposed()
    {
        var (handler, origin) = Create(new StreamLimiter(maxConcurrent: 1, maxOpensPerSecond: 1000));
        using var _ = origin;
        var accept1 = origin.AcceptAsync();
        var first = await handler.HandleAsync(new MuxOpenRequest("site.test", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.True(first.IsOk);
        using var far1 = await accept1.WaitAsync(Timeout);

        var second = await handler.HandleAsync(new MuxOpenRequest("site.test", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.Limit, second.Reason);

        await first.Target!.DisposeAsync();
        var accept3 = origin.AcceptAsync();
        var third = await handler.HandleAsync(new MuxOpenRequest("site.test", origin.Port), CancellationToken.None).WaitAsync(Timeout);
        Assert.True(third.IsOk);
        using var far3 = await accept3.WaitAsync(Timeout);
        await third.Target!.DisposeAsync();
    }

    [Fact]
    public async Task RateLimit_ReturnsLimit()
    {
        long now = 0;
        var (handler, origin) = Create(new StreamLimiter(maxConcurrent: 256, maxOpensPerSecond: 2, nowTicks: () => now));
        using var _ = origin;
        var d1 = await handler.HandleAsync(new MuxOpenRequest("other.test", 443), CancellationToken.None);
        var d2 = await handler.HandleAsync(new MuxOpenRequest("other.test", 443), CancellationToken.None);
        var d3 = await handler.HandleAsync(new MuxOpenRequest("other.test", 443), CancellationToken.None);
        Assert.Equal(OpenFailReason.NotAllowed, d1.Reason);
        Assert.Equal(OpenFailReason.NotAllowed, d2.Reason);
        Assert.Equal(OpenFailReason.Limit, d3.Reason);
    }

    [Fact]
    public void Attach_SetsAcceptorHandler()
    {
        var (handler, origin) = Create();
        using var _ = origin;
        var acceptor = new FakeAcceptor();
        handler.Attach(acceptor);
        Assert.NotNull(acceptor.OpenRequested);
    }

    [Fact]
    public void DomainCollector_DedupesAndSorts()
    {
        var c = new DomainCollector();
        Assert.True(c.Add("b.example"));
        Assert.True(c.Add("a.example"));
        Assert.False(c.Add("b.example"));
        Assert.Equal(new[] { "a.example", "b.example" }, c.Snapshot());
        Assert.True(c.Contains("a.example"));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void ByteCounter_Accumulates()
    {
        var c = new ByteCounter();
        c.AddUp(5);
        c.AddDown(7);
        c.AddUp(1);
        Assert.Equal((6L, 7L), c.Snapshot());
    }

    private sealed class FakeAcceptor : IMuxAcceptor
    {
        public Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
        public MuxStats Stats => default;
        public Task Completion => Task.CompletedTask;
        public GoAwayReason? RemoteGoAway => null;
        public Task<TimeSpan> PingAsync(CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
        public Task CloseAsync(GoAwayReason reason) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
