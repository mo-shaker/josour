using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Core.Browser;
using Josour.Proxy;
using Josour.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

/// <summary>
/// The owner check is fail-closed by design on Windows: a connection whose owning process cannot be
/// identified is refused, because the proxy listens on loopback and any program on the machine can
/// reach it. That is right. What is not right is a wiring where nothing can *ever* be identified.
///
/// <para>It shipped: the app passed the work browser to the proxy and left the checker at its default,
/// which answers "I don't know" to every question. Every connection the browser made was refused, the
/// probe page never arrived, and the session ended as browser_not_proxied with no other symptom. The
/// counters said it plainly once they were surfaced - accepted=33 rejected_by_owner=33 - but the wiring
/// itself should never have been constructible.</para>
/// </summary>
public class OwnerCheckWiringTests
{
    private sealed class FakeBrowser : IBrowserSession
    {
        public bool IsRunning => true;
        public bool OwnsProcess(int pid) => true;
        public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
            => Task.FromResult(new BrowserLaunchResult(true, null, null));
        public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ConnectProxyOptions Options(
        IBrowserSession? browser, IOwnerPidChecker? checker, bool? rejectUnknown) => new()
    {
        Allowlist = AllowlistMatcher.Parse(1, Array.Empty<string>()),
        Browser = browser,
        OwnerPidChecker = checker ?? PermissiveOwnerPidChecker.Instance,
        RejectUnknownOwner = rejectUnknown,
        SystemProxy = NoSystemProxy.Instance,
        AddressBlocker = _ => false,
    };

    [Fact]
    public void AProxyThatCouldNeverAdmitAnyoneRefusesToStart()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new ConnectProxyServer(Options(new FakeBrowser(), PermissiveOwnerPidChecker.Instance, rejectUnknown: true)));
        Assert.Contains("refuse every connection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePermissiveCheckerIsFineWhenUnknownOwnersAreAdmitted()
    {
        // In-process tests and the spike tool legitimately want "admit everything"; they say so.
        await using var proxy = new ConnectProxyServer(
            Options(new FakeBrowser(), PermissiveOwnerPidChecker.Instance, rejectUnknown: false));
        Assert.InRange(proxy.Port, 1, 65535);
    }

    [Fact]
    public async Task NoBrowserMeansNoOwnerCheckAtAll()
    {
        await using var proxy = new ConnectProxyServer(Options(browser: null, checker: null, rejectUnknown: true));
        Assert.InRange(proxy.Port, 1, 65535);
    }

    private static TunnelProxyContext Context() => new(
        AllowlistMatcher.Parse(1, Array.Empty<string>()), new[] { 80, 443 }, DnsHostResolver.Instance,
        "198.51.100.9", new FakeMux());

    [Fact]
    public async Task TheAdapterInstallsAWorkingCheckerWhereThePlatformHasOne()
    {
        // The original defect: the App calls this overload without a checker. Where the platform can answer,
        // the default has to be one that does, or the proxy is dead on arrival.
        if (!OwnerPidCheckers.SupportedOnThisPlatform)
        {
            return;
        }

        await using var proxy = ProxyTunnelAdapter.Create(Context(), new FakeBrowser(), systemProxy: NoSystemProxy.Instance);
        Assert.InRange(proxy.Port, 1, 65535);
    }

    [Fact]
    public void WhereThePlatformHasNoCheckerTheProxyRefusesToStart()
    {
        // This test used to pass everywhere, and that was the bug: off Windows the adapter installed a checker
        // that identifies nobody while the default admitted unknown owners, so the proxy quietly served every
        // process on the machine. A guest role that cannot tell the work browser from the rest of the machine
        // must refuse to exist, not widen silently.
        if (OwnerPidCheckers.SupportedOnThisPlatform)
        {
            return;
        }

        var error = Assert.Throws<ArgumentException>(
            () => ProxyTunnelAdapter.Create(Context(), new FakeBrowser(), systemProxy: NoSystemProxy.Instance));
        Assert.Contains("refuse every connection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOwnersAreRefusedByDefaultOnEveryPlatform()
    {
        // The default is fail-closed everywhere. "Admit everything" remains available to whoever says so.
        await using var admitting = new ConnectProxyServer(
            Options(new FakeBrowser(), PermissiveOwnerPidChecker.Instance, rejectUnknown: false));
        Assert.InRange(admitting.Port, 1, 65535);

        Assert.Throws<ArgumentException>(
            () => new ConnectProxyServer(Options(new FakeBrowser(), PermissiveOwnerPidChecker.Instance, rejectUnknown: null)));
    }
}
