using Microsoft.Extensions.Logging;
using Josour.Browser;
using Josour.Core.Browser;
using Josour.Infrastructure.Session;

namespace Josour.App.Services;

/// <summary>
/// A work-browser session that leaves a marker on disk while its browser is running, and removes it when the browser is
/// gone. That marker is what <see cref="StaleWorkBrowserGuard"/> reads at the next start-up: if the app died without
/// closing the browser, the next run finds it and closes it before starting a session of its own
/// (plan 8.5: the application crashing).
/// <para>
/// It wraps <see cref="BrowserLauncher"/> rather than living inside it because the launcher belongs to Track B and the
/// process identity (id, name, start time) is all this needs from it — <see cref="BrowserLauncher.ProcessId"/> plus one
/// look at the process table through the same <see cref="IProcessController"/> the guard uses.
/// </para>
/// </summary>
public sealed class MarkedBrowserSession : IBrowserSession
{
    private readonly IBrowserSession _inner;
    private readonly IWorkBrowserRunMarkerStore _markers;
    private readonly IProcessController _processes;
    private readonly string _profileDirectory;
    private readonly ILogger _logger;

    public MarkedBrowserSession(
        IBrowserSession inner,
        IWorkBrowserRunMarkerStore markers,
        IProcessController processes,
        string profileDirectory,
        ILogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _profileDirectory = profileDirectory;
        _logger = logger;
    }

    public bool IsRunning => _inner.IsRunning;

    public bool OwnsProcess(int pid) => _inner.OwnsProcess(pid);

    public async Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        var result = await _inner.LaunchAsync(options, ct).ConfigureAwait(false);
        if (result.Success)
        {
            Mark();
        }

        return result;
    }

    public async Task CloseAsync(TimeSpan graceful, CancellationToken ct)
    {
        try
        {
            await _inner.CloseAsync(graceful, ct).ConfigureAwait(false);
        }
        finally
        {
            _markers.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _markers.Clear();
        }
    }

    private void Mark()
    {
        var pid = (_inner as BrowserLauncher)?.ProcessId;
        if (pid is not int processId)
        {
            _logger.LogDebug("The work browser reported no process id; nothing to mark for the next start-up");
            return;
        }

        // Name and start time go into the marker so a recycled process id cannot be mistaken for our browser later.
        if (_processes.Find(processId) is not { } snapshot)
        {
            _logger.LogDebug("Work-browser process {ProcessId} disappeared before it could be marked", processId);
            return;
        }

        _markers.Write(new WorkBrowserRunMarker(
            snapshot.Id,
            snapshot.Name,
            snapshot.StartedAtUtc,
            _profileDirectory,
            DateTimeOffset.UtcNow));
    }
}
