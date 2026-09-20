using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.Session;

/// <summary>One live process, as much of it as the guard needs to recognise it again after a restart.</summary>
public sealed record ProcessSnapshot(int Id, string Name, DateTimeOffset StartedAtUtc);

/// <summary>The bit of the operating system the guard uses; a fake stands in for it in the tests.</summary>
public interface IProcessController
{
    /// <summary>The process with that id, or null when nothing is running under it.</summary>
    ProcessSnapshot? Find(int processId);

    /// <summary>Asks it to close, then kills it (and its children) if it does not. True when it is gone afterwards.</summary>
    Task<bool> CloseAsync(int processId, TimeSpan graceful, CancellationToken ct);
}

/// <summary>What <see cref="StaleWorkBrowserGuard.CleanupAsync"/> found and did; the app logs it, the tests assert on it.</summary>
/// <param name="MarkerFound">A marker from a previous run existed at all, i.e. the last run did not exit cleanly.</param>
/// <param name="ProcessFound">The process in the marker was still running (and really was that process).</param>
/// <param name="Closed">It was closed.</param>
/// <param name="ProfileArtifactsRemoved">Stale Chromium singleton files removed from the work profile.</param>
public sealed record StaleWorkBrowserCleanup(bool MarkerFound, bool ProcessFound, bool Closed, int ProfileArtifactsRemoved)
{
    public static StaleWorkBrowserCleanup Nothing { get; } = new(false, false, false, 0);
}

/// <summary>
/// Start-up guard for the plan's "the application crashing" case (8.5): a work browser left over from a previous run is closed
/// BEFORE this run starts, so no browser window keeps pointing at a local proxy that died with the process — from the
/// user's side it still looks like a working session, while nothing is filtered or tunnelled any more.
/// <para>
/// The marker (<see cref="IWorkBrowserRunMarkerStore"/>) names the process the last run launched. Before touching it the
/// guard checks that the process id still belongs to the SAME process (name and start time), because on a machine that
/// has been up for a while a recycled PID could easily be something else — killing that would be a bug with teeth.
/// </para>
/// <para>
/// It also clears the work profile's Chromium singleton files. They are how a second Chrome finds a first one; left
/// behind by a crash they make the next launch hand itself off to a process that no longer exists, which the launcher can
/// only report as <c>InstanceHandoff</c>. Removing them is safe: the profile belongs to Josour alone, and by this
/// point nothing is using it.
/// </para>
/// <para>The marker is cleared either way, so a start-up never has to do this twice.</para>
/// </summary>
public sealed class StaleWorkBrowserGuard
{
    /// <summary>Chromium's process-singleton artifacts inside a user-data-dir.</summary>
    private static readonly string[] SingletonArtifacts = { "SingletonLock", "SingletonCookie", "SingletonSocket" };

    /// <summary>Start times are recorded with second-ish resolution on Windows; anything closer is the same process.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private readonly IWorkBrowserRunMarkerStore _markers;
    private readonly IProcessController _processes;
    private readonly ILogger _logger;
    private readonly TimeSpan _graceful;

    public StaleWorkBrowserGuard(
        IWorkBrowserRunMarkerStore markers,
        IProcessController processes,
        ILogger<StaleWorkBrowserGuard>? logger = null,
        TimeSpan? graceful = null)
    {
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _graceful = graceful ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Runs the guard. Never throws: a start-up must not fail because of a leftover from the last one.</summary>
    public async Task<StaleWorkBrowserCleanup> CleanupAsync(CancellationToken ct)
    {
        WorkBrowserRunMarker? marker;
        try
        {
            marker = _markers.Read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the work-browser marker; skipping the start-up clean-up");
            return StaleWorkBrowserCleanup.Nothing;
        }

        if (marker is null)
        {
            _logger.LogDebug("No work browser was left behind by a previous run");
            return StaleWorkBrowserCleanup.Nothing;
        }

        _logger.LogWarning(
            "A previous run left a work browser behind ({ProcessName}, pid {ProcessId}, started {StartedAt:O}): the last exit was not clean",
            marker.ProcessName,
            marker.ProcessId,
            marker.StartedAtUtc);

        var found = false;
        var closed = false;
        try
        {
            if (Matches(marker, _processes.Find(marker.ProcessId)))
            {
                found = true;
                closed = await _processes.CloseAsync(marker.ProcessId, _graceful, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    closed
                        ? "Closed the work browser left over from the previous run ({ProcessName}, pid {ProcessId})"
                        : "The work browser left over from the previous run ({ProcessName}, pid {ProcessId}) could not be closed",
                    marker.ProcessName,
                    marker.ProcessId);
            }
            else
            {
                _logger.LogInformation("The process from the marker is gone (or the id belongs to something else now): nothing to close");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the leftover work browser failed");
        }

        var removed = RemoveProfileArtifacts(marker.ProfileDirectory);
        _markers.Clear();
        return new StaleWorkBrowserCleanup(MarkerFound: true, found, closed, removed);
    }

    /// <summary>The id must still be the process we started: same name, same start time (within the clock's resolution).</summary>
    private bool Matches(WorkBrowserRunMarker marker, ProcessSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        if (!string.Equals(snapshot.Name, marker.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Process {ProcessId} is now {ActualName}, not {ExpectedName}: the id was reused, leaving it alone",
                marker.ProcessId,
                snapshot.Name,
                marker.ProcessName);
            return false;
        }

        var drift = (snapshot.StartedAtUtc - marker.StartedAtUtc).Duration();
        if (drift > StartTimeTolerance)
        {
            _logger.LogInformation(
                "Process {ProcessId} started {Drift:0.#} s away from the marker: the id was reused, leaving it alone",
                marker.ProcessId,
                drift.TotalSeconds);
            return false;
        }

        return true;
    }

    /// <summary>Removes the singleton files a crashed Chromium leaves in the work profile; returns how many were removed.</summary>
    private int RemoveProfileArtifacts(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var name in SingletonArtifacts)
        {
            try
            {
                var path = Path.Combine(profileDirectory, name);

                // SingletonLock is a symlink on some builds: Exists() is false for a dangling one, so ask the link too.
                if (File.Exists(path) || File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogDebug("Could not remove the stale {Artifact} of the work profile: {Message}", name, ex.Message);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} stale Chromium singleton file(s) from the work profile", removed);
        }

        return removed;
    }
}

/// <summary>
/// <see cref="IProcessController"/> over <see cref="Process"/>: a polite close (Windows: <c>WM_CLOSE</c> to the main
/// window) with a grace period, then <see cref="Process.Kill(bool)"/> on the whole tree — the same order the work
/// browser's own <c>GracefulCloser</c> uses, because a browser killed outright leaves its profile marked as crashed.
/// </summary>
public sealed class SystemProcessController : IProcessController
{
    private readonly ILogger _logger;

    public SystemProcessController(ILogger<SystemProcessController>? logger = null) =>
        _logger = (ILogger?)logger ?? NullLogger.Instance;

    public ProcessSnapshot? Find(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }

            return new ProcessSnapshot(process.Id, process.ProcessName, process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // ArgumentException: no such process. The rest: it went away, or this account may not look at it.
            _logger.LogDebug("Process {ProcessId} could not be inspected: {Message}", processId, ex.Message);
            return null;
        }
    }

    public async Task<bool> CloseAsync(int processId, TimeSpan graceful, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // already gone
        }

        using (process)
        {
            try
            {
                if (graceful > TimeSpan.Zero && OperatingSystem.IsWindows())
                {
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        _logger.LogDebug("Could not ask process {ProcessId} to close politely: {Message}", processId, ex.Message);
                    }

                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(graceful);
                    try
                    {
                        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                        return true;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("Process {ProcessId} ignored the polite close; killing it", processId);
                    }
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                _logger.LogWarning("Could not close process {ProcessId}: {Message}", processId, ex.Message);
                return process.HasExited;
            }
        }
    }
}
