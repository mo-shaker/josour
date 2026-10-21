using System.Net;
using Josour.Core.Net;

namespace Josour.Core.Tests;

/// <summary>
/// While a guest session points the system proxy at our own listener, the app's own traffic must not follow it
/// (ADR-0015 point 3) — and any other proxy, a company's or a local tool's, must still be used.
/// </summary>
public sealed class LoopSafeWebProxyTests
{
    private static readonly Uri Api = new("https://josour.example.com/api/v1/me");

    [Fact]
    public void OurOwnListener_IsBypassed_WhileItIsRegistered()
    {
        var proxy = new LoopSafeWebProxy(new WebProxy("http://127.0.0.1:50991"));

        OwnProxyPorts.Add(50991);
        try
        {
            Assert.True(proxy.IsBypassed(Api));
            Assert.Null(proxy.GetProxy(Api));
        }
        finally
        {
            OwnProxyPorts.Remove(50991);
        }

        Assert.False(proxy.IsBypassed(Api));
        Assert.Equal(new Uri("http://127.0.0.1:50991/"), proxy.GetProxy(Api));
    }

    [Fact]
    public void AnotherLoopbackProxy_OrACompanyProxy_IsStillUsed()
    {
        OwnProxyPorts.Add(50992);
        try
        {
            Assert.Equal(new Uri("http://127.0.0.1:7890/"), new LoopSafeWebProxy(new WebProxy("http://127.0.0.1:7890")).GetProxy(Api));
            Assert.Equal(new Uri("http://proxy.corp:8080/"), new LoopSafeWebProxy(new WebProxy("http://proxy.corp:8080")).GetProxy(Api));
        }
        finally
        {
            OwnProxyPorts.Remove(50992);
        }
    }

    [Fact]
    public void InstallAsDefault_WrapsOnce()
    {
        var before = HttpClient.DefaultProxy;
        try
        {
            LoopSafeWebProxy.InstallAsDefault();
            var first = HttpClient.DefaultProxy;
            LoopSafeWebProxy.InstallAsDefault();
            Assert.IsType<LoopSafeWebProxy>(first);
            Assert.Same(first, HttpClient.DefaultProxy);
        }
        finally
        {
            HttpClient.DefaultProxy = before;
        }
    }
}
