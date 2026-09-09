using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Browser;

namespace Josour.Infrastructure.Session;

/// <summary>
/// Where the work browser comes from. The App implements it with <c>Josour.Browser</c> (BrowserLocator + PolicyDetector +
/// BrowserLauncher); tests return fakes. <see cref="Preference"/> is evaluated at launch time because both the installed
/// browsers and the enterprise policies can change while the app runs.
/// </summary>
public interface IWorkBrowserProvider
{
    /// <summary>The profile the work browser uses (<c>--user-data-dir</c>); never the user's own profile.</summary>
    string ProfileDirectory { get; }

    /// <summary>
    /// The browsers to try, best first: installed only, and the ones NOT managed by enterprise policy before the managed ones
    /// (a managed Chrome/Edge ignores <c>--proxy-server</c>, which is exactly what <c>browser_not_proxied</c> catches).
    /// Empty when neither Chrome nor Edge is installed.
    /// </summary>
    IReadOnlyList<BrowserKind> Preference();

    /// <summary>A fresh, not-yet-launched browser session for that kind.</summary>
    IBrowserSession Create(BrowserKind kind);
}

/// <summary>
/// The work browser as the session sees it: one stable object (the guest proxy keeps a reference for its owner-PID check)
/// that can be launched again after the user closed the browser window ("Reopen work browser").
/// </summary>
public interface IWorkBrowser : IBrowserSession
{
    /// <summary>Launches — or relaunches — the work browser on the probe page through the local proxy.</summary>
    Task<BrowserLaunchResult> OpenAsync(int proxyPort, string probeUrl, CancellationToken ct);

    /// <summary>
    /// Whether this process has a work browser at all. False only for <see cref="NoWorkBrowser"/> (head-less use): the
    /// coordinator then skips the launch and the probe wait after <c>session.active</c> instead of ending the session with
    /// <c>browser_not_proxied</c>, which would name a browser that was never started. Every real implementation leaves it true.
    /// </summary>
    bool CanLaunch => true;
}

/// <summary>
/// <see cref="IWorkBrowser"/> over <see cref="IWorkBrowserProvider"/>: each launch gets a brand-new underlying
/// <see cref="IBrowserSession"/> (a launcher owns exactly one process tree and one Job Object), while this wrapper stays the
/// same object for the whole session so the proxy's owner-PID check keeps working across a reopen.
/// <para>
/// A kind that fails with <see cref="BrowserLaunchFailure.NotFound"/> or <see cref="BrowserLaunchFailure.ManagedByPolicy"/>
/// falls through to the next preference; any other failure is returned as-is (retrying would just fail the same way).
/// </para>
/// </summary>
public sealed class WorkBrowserSession : IWorkBrowser, IDisposable
{
    private readonly IWorkBrowserProvider _provider;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IBrowserSession? _current;
    private bool _disposed;

    public WorkBrowserSession(IWorkBrowserProvider provider, ILogger<WorkBrowserSession>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The browser actually launched last, or null before the first launch.</summary>
    public BrowserKind? LaunchedKind { get; private set; }

    public bool IsRunning
    {
        get
        {
            var current = Current;
            return current is not null && current.IsRunning;
        }
    }

    public bool OwnsProcess(int pid) => Current?.OwnsProcess(pid) ?? false;

    private IBrowserSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public async Task<BrowserLaunchResult> OpenAsync(int proxyPort, string probeUrl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeUrl);
        var preference = _provider.Preference();
        if (preference.Count == 0)
        {
            return new BrowserLaunchResult(false, BrowserLaunchFailure.NotFound, "neither Chrome nor Edge is installed");
        }

        BrowserLaunchResult? last = null;
        foreach (var kind in preference)
        {
            var result = await LaunchAsync(new BrowserLaunchOptions(kind, proxyPort, _provider.ProfileDirectory, probeUrl), ct).ConfigureAwait(false);
            if (result.Success)
            {
                LaunchedKind = kind;
                _logger.LogInformation("Work browser launched: {Kind} on 127.0.0.1:{ProxyPort} at {ProbeUrl}", kind, proxyPort, probeUrl);
                return result;
            }

            last = result;
            _logger.LogWarning("Work browser {Kind} could not start: {Failure} ({Detail})", kind, result.Failure, result.Detail);
            if (result.Failure is not (BrowserLaunchFailure.NotFound or BrowserLaunchFailure.ManagedByPolicy))
            {
                break;
            }
        }

        return last ?? new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "no browser was tried");
    }

    /// <summary>Launches with explicit options (the kind decides which underlying launcher is created).</summary>
    public async Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_disposed)
            {
                return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "the work browser was disposed");
            }
        }

        // A previous window (or a failed attempt) must be gone before the new launcher takes over the profile directory.
        await CloseCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);

        var session = _provider.Create(options.Kind);
        lock (_gate)
        {
            if (_disposed)
            {
                _ = session.DisposeAsync();
                return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "the work browser was disposed");
            }

            _current = session;
        }

        var result = await session.LaunchAsync(options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            await CloseCurrentAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Cleanup step 2 of docs/protocol.md section 7: polite close, then the Job Object. Safe to call repeatedly.</summary>
    public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => CloseCurrentAsync(graceful, ct);

    private async Task CloseCurrentAsync(TimeSpan graceful, CancellationToken ct)
    {
        IBrowserSession? session;
        lock (_gate)
        {
            session = _current;
            _current = null;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            await session.CloseAsync(graceful, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Closing the work browser failed");
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing the work browser failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        await CloseCurrentAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Also synchronous, on purpose: a DI container that is torn down with <c>Dispose</c> refuses a singleton that only
    /// implements <see cref="IAsyncDisposable"/>, and the browser must go away even on that path.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
