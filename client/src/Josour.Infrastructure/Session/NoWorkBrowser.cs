using Josour.Core.Browser;

namespace Josour.Infrastructure.Session;

/// <summary>
/// The work browser of a process that has none. <see cref="SessionCoordinator"/> always needs an <see cref="IWorkBrowser"/>,
/// and a head-less consumer (the QA tool's <c>--curl-test</c>, an automated run) has no browser to give it; this one answers
/// "no browser" cleanly instead of pretending to launch one.
///
/// <para><b>The rule.</b> <c>browser_not_proxied</c> means a work browser <i>was</i> launched and did not come through our
/// local proxy (docs/ws-protocol.md section 5). With no browser at all there is nothing that could bypass the proxy, so the
/// coordinator skips both the launch and the probe wait after <c>session.active</c> (<see cref="IWorkBrowser.CanLaunch"/> is
/// false here) and the session proceeds: the proxy is still verified by whatever the caller then sends through
/// <c>ITunnelSession.Proxy.Port</c>. With a real browser nothing changes — it is launched, the probe is awaited, and a probe
/// that never arrives still ends the session with <c>browser_not_proxied</c>.</para>
///
/// <para>The guest proxy must be wired <b>without</b> this instance (pass a null browser to <c>ProxyTunnelAdapter.Create</c>):
/// there is no browser process that could own the local connections, so the owner-PID check would reject the very traffic the
/// caller means to send through the proxy.</para>
/// </summary>
public sealed class NoWorkBrowser : IWorkBrowser
{
    /// <summary>The detail every refused launch carries; also what the caller sees in <c>BrowserFailureDetail</c>.</summary>
    public const string NoBrowserDetail = "this process runs without a work browser";

    private NoWorkBrowser()
    {
    }

    /// <summary>The single instance; it holds no state.</summary>
    public static NoWorkBrowser Instance { get; } = new();

    /// <summary>Always false: there is nothing to launch, so the coordinator must not wait for a probe page.</summary>
    public bool CanLaunch => false;

    public bool IsRunning => false;

    public bool OwnsProcess(int pid) => false;

    public Task<BrowserLaunchResult> OpenAsync(int proxyPort, string probeUrl, CancellationToken ct) => Task.FromResult(Refused);

    public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct) => Task.FromResult(Refused);

    public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static BrowserLaunchResult Refused => new(false, BrowserLaunchFailure.NotFound, NoBrowserDetail);
}
