using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.SystemProxy;

/// <summary>
/// The body of the guardian process (<c>Josour --proxy-guardian &lt;pid&gt; &lt;start&gt;</c>): a second copy of the app
/// with no window that watches the one that changed the system proxy. If that one dies with its marker still on disk —
/// killed, crashed — the guardian puts the settings back. It leaves on its own as soon as the marker is gone or belongs to
/// another process.
/// </summary>
public static class SystemProxyGuardian
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <returns>0 when there was nothing left to do or the settings were restored; 1 when a restore failed.</returns>
    public static async Task<int> RunAsync(
        int parentPid,
        DateTimeOffset parentStartedAt,
        ISystemProxyGuard guard,
        ISystemProxyMarkerStore markers,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(markers);
        logger ??= NullLogger.Instance;
        while (!ct.IsCancellationRequested)
        {
            var marker = markers.Read();
            if (marker is null || marker.ProcessId != parentPid) return 0;
            if (!IsAlive(parentPid, parentStartedAt))
            {
                logger.LogWarning("Josour ({Pid}) ended with the system proxy still pointed at it; putting the settings back", parentPid);
                return await guard.RestoreAsync(CancellationToken.None).ConfigureAwait(false) ? 0 : 1;
            }
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    /// <summary>Is the process with this PID the one that started at that time (and not a later one that reused the PID)?</summary>
    public static bool IsAlive(int pid, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            var start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (start - startedAt).Duration() < TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return true; // cannot tell (access denied): assume it lives rather than restoring under it
        }
    }
}
