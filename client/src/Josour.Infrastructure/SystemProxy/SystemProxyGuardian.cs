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

    /// <summary>How far the start time in the marker may be from the one on the command line and still be the same process.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

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

        // The session this guardian was started for. A later session of the same app writes a marker of its own and starts
        // a guardian of its own, so this one leaves when the marker it watches is replaced.
        DateTimeOffset? watched = null;
        while (!ct.IsCancellationRequested)
        {
            if (!markers.Exists) return 0;
            var marker = markers.Read();
            if (marker is null)
            {
                // On disk but unreadable this moment (another program holds the file): not a reason to stop watching.
                if (!IsAlive(parentPid, parentStartedAt))
                {
                    logger.LogError("Josour ({Pid}) ended and its system-proxy marker cannot be read; the next start or login restores", parentPid);
                    return 1;
                }
            }
            else
            {
                if (marker.ProcessId != parentPid || (marker.ProcessStartedAt - parentStartedAt).Duration() > StartTolerance) return 0;
                watched ??= marker.WrittenAt;
                if (marker.WrittenAt.UtcTicks != watched.Value.UtcTicks) return 0;

                if (!IsAlive(parentPid, parentStartedAt))
                {
                    logger.LogWarning("Josour ({Pid}) ended with the system proxy still pointed at it; putting the settings back", parentPid);
                    if (await guard.RestoreIfAsync(marker, CancellationToken.None).ConfigureAwait(false)) return 0;

                    // Not restored here: fine when someone else did it, or the marker belongs to a newer session now.
                    var now = markers.Read();
                    return !markers.Exists || (now is not null && !marker.IsSameRunAs(now)) ? 0 : 1;
                }
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
