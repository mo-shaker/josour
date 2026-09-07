using System.Net;
using Josour.Core.Net;

namespace Josour.Core.Tests;

/// <summary>IWebProxy ملفَّق: يمثل ما يعيده WinHTTP على Windows أو متغيرات البيئة على غيرها.</summary>
internal sealed class FakeWebProxy : IWebProxy
{
    private readonly Uri? _proxy;
    private readonly Func<Uri, bool>? _bypass;

    public FakeWebProxy(string? proxy, Func<Uri, bool>? bypass = null)
    {
        _proxy = proxy is null ? null : new Uri(proxy);
        _bypass = bypass;
    }

    public ICredentials? Credentials { get; set; }
    public bool Throws { get; init; }

    public Uri? GetProxy(Uri destination) => Throws ? throw new InvalidOperationException("pac script failed") : _proxy;
    public bool IsBypassed(Uri host) => Throws ? throw new InvalidOperationException("pac script failed") : _bypass?.Invoke(host) ?? false;
}

public class SystemProxyResolverTests
{
    private static SystemProxyResolver Resolver(IWebProxy? proxy) => new(() => proxy);

    [Fact]
    public void NoProxyConfigured_ResolvesToNull()
    {
        Assert.Null(Resolver(null).Resolve(new Uri("https://example.com/")));
        Assert.False(Resolver(null).IsConfigured);
    }

    [Fact]
    public void ConfiguredProxy_IsReturnedWithHostAndPort()
    {
        var resolver = Resolver(new FakeWebProxy("http://proxy.corp.example:8080"));
        var endpoint = resolver.Resolve(new Uri("https://news.example/"));
        Assert.NotNull(endpoint);
        Assert.Equal("proxy.corp.example", endpoint!.Host);
        Assert.Equal(8080, endpoint.Port);
        Assert.True(resolver.IsConfigured);
        Assert.Equal("proxy.corp.example:8080", endpoint.ToString());
    }

    [Fact]
    public void BypassList_IsHonoured()
    {
        var resolver = Resolver(new FakeWebProxy(
            "http://proxy.corp.example:8080",
            uri => uri.Host.EndsWith(".internal.example", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(resolver.Resolve(new Uri("https://news.example/")));
        Assert.Null(resolver.Resolve(new Uri("https://wiki.internal.example/")));
    }

    [Fact]
    public void LoopbackDestination_NeverGoesThroughAProxy()
    {
        var resolver = Resolver(new FakeWebProxy("http://proxy.corp.example:8080"));
        Assert.Null(resolver.Resolve(new Uri("http://127.0.0.1:9000/")));
        Assert.Null(resolver.Resolve(new Uri("http://localhost:9000/")));
    }

    [Fact]
    public void ProxyPointingAtTheDestinationItself_MeansNoProxy()
    {
        var resolver = Resolver(new FakeWebProxy("http://news.example:80"));
        Assert.Null(resolver.Resolve(new Uri("http://news.example/")));
    }

    [Fact]
    public void PrivateOrLoopbackProxyAddress_IsAllowed()
    {
        // Proxy الشركات يسكن عادةً 10.x أو 127.0.0.1؛ مصدره إعداد الجهاز لا الطلب، فلا يخضع لحظر العناوين الخاصة.
        Assert.Equal("10.0.0.8", Resolver(new FakeWebProxy("http://10.0.0.8:3128")).Resolve(new Uri("https://news.example/"))!.Host);
        Assert.Equal("127.0.0.1", Resolver(new FakeWebProxy("http://127.0.0.1:8888")).Resolve(new Uri("https://news.example/"))!.Host);
    }

    [Fact]
    public void NonHttpProxyScheme_IsIgnored()
    {
        // SOCKS لا يتكلمه هذا المسار؛ التظاهر بأنه HTTP يعطي فشلًا صامتًا.
        Assert.Null(Resolver(new FakeWebProxy("socks5://proxy.corp.example:1080")).Resolve(new Uri("https://news.example/")));
    }

    [Fact]
    public void ThrowingProxy_ResolvesToNull()
    {
        var resolver = Resolver(new FakeWebProxy("http://proxy.corp.example:8080") { Throws = true });
        Assert.Null(resolver.Resolve(new Uri("https://news.example/")));
        Assert.False(resolver.IsConfigured);
    }

    [Fact]
    public void ThrowingProxySource_ResolvesToNull()
    {
        var resolver = new SystemProxyResolver(() => throw new InvalidOperationException("registry unavailable"));
        Assert.Null(resolver.Resolve(new Uri("https://news.example/")));
    }

    [Fact]
    public void NoSystemProxy_AlwaysNull()
    {
        Assert.Null(NoSystemProxy.Instance.Resolve(new Uri("https://news.example/")));
        Assert.False(NoSystemProxy.Instance.IsConfigured);
    }

    [Fact]
    public void StaticSystemProxy_AppliesItsOwnBypass()
    {
        var proxy = new StaticSystemProxy(new SystemProxyEndpoint("127.0.0.1", 8080), uri => uri.Host == "skip.example");
        Assert.Equal(8080, proxy.Resolve(new Uri("https://news.example/"))!.Port);
        Assert.Null(proxy.Resolve(new Uri("https://skip.example/")));
        Assert.True(proxy.IsConfigured);
    }

    [Fact]
    public void DefaultResolver_NeverThrows()
    {
        // يقرأ إعدادات الجهاز الحقيقية: القيمة تختلف بالبيئة، لكن لا يرمي.
        var endpoint = SystemProxyResolver.Default.Resolve(new Uri("https://example.com/"));
        Assert.True(endpoint is null || endpoint.Port is > 0 and <= 65535);
    }
}
