using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Egress.Tests;

public class EgressPolicyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static EgressPolicy Create(StubResolver? resolver = null, Func<IPAddress, bool>? blocker = null, IReadOnlyList<IPAddress>? locals = null, params string[] entries)
    {
        var allowlist = AllowlistMatcher.Parse(1, entries.Length == 0 ? new[] { "allowed.example", "=exact.example", "portal.example:8443" } : entries);
        return new EgressPolicy(allowlist, new EgressPolicyOptions
        {
            Resolver = resolver ?? new StubResolver(),
            AddressBlocker = blocker,
            LocalAddresses = locals ?? Array.Empty<IPAddress>(),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        });
    }

    [Theory]
    [InlineData("127.0.0.1", 443, OpenFailReason.IpLiteral)]
    [InlineData("[::1]", 443, OpenFailReason.IpLiteral)]
    [InlineData("::ffff:10.0.0.1", 443, OpenFailReason.IpLiteral)]
    [InlineData("93.184.216.34", 80, OpenFailReason.IpLiteral)]
    [InlineData("other.example", 443, OpenFailReason.NotAllowed)]
    [InlineData("notallowed.example", 443, OpenFailReason.NotAllowed)]
    [InlineData("sub.exact.example", 443, OpenFailReason.NotAllowed)]
    [InlineData("allowed.example", 8080, OpenFailReason.PortNotAllowed)]
    [InlineData("portal.example", 443, OpenFailReason.PortNotAllowed)]
    [InlineData("", 443, OpenFailReason.NotAllowed)]
    [InlineData("bad host", 443, OpenFailReason.NotAllowed)]
    public void Evaluate_Steps1To4_Table(string host, int port, OpenFailReason expected)
    {
        var policy = Create();
        Assert.Equal(expected, policy.Evaluate(host, port, out _));
    }

    [Theory]
    [InlineData("allowed.example", 443, "allowed.example")]
    [InlineData("ALLOWED.Example.", 80, "allowed.example")]
    [InlineData("a.b.allowed.example", 443, "a.b.allowed.example")]
    [InlineData("exact.example", 443, "exact.example")]
    [InlineData("portal.example", 8443, "portal.example")]
    [InlineData("مثال.allowed.example", 443, "xn--mgbh0fb.allowed.example")]
    public void Evaluate_Allowed_ReturnsNormalizedHost(string host, int port, string normalized)
    {
        var policy = Create();
        Assert.Null(policy.Evaluate(host, port, out var n));
        Assert.Equal(normalized, n);
    }

    [Fact]
    public async Task IpLiteral_NeverResolves()
    {
        var resolver = new StubResolver();
        var policy = Create(resolver);
        var result = await policy.OpenAsync("10.0.0.1", 443, CancellationToken.None);
        Assert.Equal(OpenFailReason.IpLiteral, result.Reason);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task DnsFailure_IsDnsFailed()
    {
        var resolver = new StubResolver(); // it knows nothing
        var policy = Create(resolver);
        var result = await policy.OpenAsync("allowed.example", 443, CancellationToken.None);
        Assert.Equal(OpenFailReason.DnsFailed, result.Reason);
        Assert.Equal("allowed.example", result.NormalizedHost);
    }

    [Fact]
    public async Task EmptyResolution_IsDnsFailed()
    {
        var resolver = new StubResolver().Map("allowed.example");
        var policy = Create(resolver);
        Assert.Equal(OpenFailReason.DnsFailed, (await policy.OpenAsync("allowed.example", 443, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task DnsTimeout_IsDnsFailed()
    {
        var slow = new SlowResolver();
        var allowlist = AllowlistMatcher.Parse(1, new[] { "allowed.example" });
        var policy = new EgressPolicy(allowlist, new EgressPolicyOptions { Resolver = slow, DnsTimeout = TimeSpan.FromMilliseconds(200) });
        var result = await policy.OpenAsync("allowed.example", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.DnsFailed, result.Reason);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("64:ff9b::c0a8:1")]
    public async Task BlockedRangeAfterResolve_IsPrivateIp(string resolved)
    {
        var resolver = new StubResolver().Map("allowed.example", resolved);
        var policy = Create(resolver);
        var result = await policy.OpenAsync("allowed.example", 443, CancellationToken.None);
        Assert.Equal(OpenFailReason.PrivateIp, result.Reason);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task AnyBlockedAddressAmongResults_IsPrivateIp()
    {
        var resolver = new StubResolver().Map("allowed.example", "93.184.216.34", "192.168.1.10");
        var policy = Create(resolver);
        Assert.Equal(OpenFailReason.PrivateIp, (await policy.OpenAsync("allowed.example", 443, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task HostsOwnPublicIp_IsPrivateIp()
    {
        var resolver = new StubResolver().Map("allowed.example", "8.8.8.8");
        var policy = Create(resolver, locals: new[] { IPAddress.Parse("8.8.8.8") });
        Assert.Equal(OpenFailReason.PrivateIp, (await policy.OpenAsync("allowed.example", 443, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task ConnectRefused_IsConnectFailed()
    {
        var port = LocalOrigin.ClosedPort();
        var resolver = new StubResolver().Map("allowed.example", "127.0.0.1");
        var policy = Create(resolver, Policies.AllowLoopbackOnly, null, $"allowed.example:{port}");
        var result = await policy.OpenAsync("allowed.example", port, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.ConnectFailed, result.Reason);
        Assert.Equal("allowed.example", result.NormalizedHost);
    }

    [Fact]
    public async Task Ok_ConnectsToValidatedAddressOnly_AndStreamIsHalfClosable()
    {
        using var origin = new LocalOrigin();
        var resolver = new StubResolver().Map("allowed.example", "127.0.0.1");
        var policy = Create(resolver, Policies.AllowLoopbackOnly, null, $"allowed.example:{origin.Port}");
        var accept = origin.AcceptAsync();

        var result = await policy.OpenAsync("Allowed.Example.", origin.Port, CancellationToken.None).WaitAsync(Timeout);

        Assert.True(result.IsOk, result.Reason?.ToString());
        Assert.Equal("allowed.example", result.NormalizedHost);
        Assert.Equal(IPAddress.Loopback, result.ConnectedTo);
        Assert.Equal(new[] { "allowed.example" }, resolver.Requested);
        using var far = await accept.WaitAsync(Timeout);
        await using var stream = result.Stream!;
        Assert.IsAssignableFrom<IHalfClosable>(stream);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("hi"));
        var buffer = new byte[2];
        await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("hi", Encoding.ASCII.GetString(buffer));
        await ((IHalfClosable)stream).CompleteWritingAsync(CancellationToken.None);
        Assert.Equal(0, await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout));
    }

    [Fact]
    public async Task ExternalCancellation_Propagates()
    {
        var slow = new SlowResolver();
        var allowlist = AllowlistMatcher.Parse(1, new[] { "allowed.example" });
        var policy = new EgressPolicy(allowlist, new EgressPolicyOptions { Resolver = slow });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.OpenAsync("allowed.example", 443, cts.Token));
    }

    private sealed class SlowResolver : IHostResolver
    {
        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return Array.Empty<IPAddress>();
        }
    }
}
