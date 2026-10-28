using Josour.Infrastructure.Logging;
using Josour.Infrastructure.SystemProxy;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Josour.App.Services;

/// <summary>
/// The two window-less runs of the executable that put the system proxy back (ADR-0015): the guardian beside a running
/// app, and the restore at login. They log to their own file (<c>logs/proxy-*.log</c>), because the app's log file is
/// held open by the app.
/// </summary>
public static class SystemProxyCommands
{
    public static int RunGuardian(ProxyGuardianTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var loggers = CreateLoggers();
        var logger = loggers.CreateLogger("Josour.SystemProxyGuardian");
        try
        {
            var (guard, markers) = Build(loggers);
            logger.LogInformation("Guarding the system proxy for Josour ({Pid})", target.ProcessId);
            return SystemProxyGuardian.RunAsync(target.ProcessId, target.StartedAt, guard, markers, logger).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The system-proxy guardian failed");
            return 1;
        }
    }

    public static int Restore()
    {
        using var loggers = CreateLoggers();
        var logger = loggers.CreateLogger("Josour.SystemProxyRestore");
        try
        {
            var (guard, markers) = Build(loggers);
            if (markers.Read() is not { } marker) return 0;
            // The app itself may still be running (a restore at login racing a fast auto-start): its own session is not ours to end.
            if (SystemProxyGuardian.IsAlive(marker.ProcessId, marker.ProcessStartedAt)) return 0;
            // Only the marker it looked at: an app that started a session since (it took the lock) is not this run's to undo.
            return guard.RestoreIfAsync(marker, CancellationToken.None).GetAwaiter().GetResult() || !markers.Exists ? 0 : 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restoring the system proxy failed");
            return 1;
        }
    }

    private static (ISystemProxyGuard Guard, ISystemProxyMarkerStore Markers) Build(ILoggerFactory loggers)
    {
        var markers = new SystemProxyMarkerStore(loggers.CreateLogger<SystemProxyMarkerStore>());
        var guard = new SystemProxyGuard(
            SystemProxyControllers.ForCurrentPlatform(loggers),
            markers,
            new SystemProxySafetyNet(loggers.CreateLogger<SystemProxySafetyNet>()),
            loggers.CreateLogger<SystemProxyGuard>(),
            // These runs have time to wait out the app's own restore — or a macOS password prompt it is showing.
            lockTimeout: TimeSpan.FromMinutes(5));
        return (guard, markers);
    }

    private static SerilogLoggerFactory CreateLoggers()
        => new(LoggingSetup.CreateConfiguration(fileNamePrefix: "proxy-").CreateLogger(), dispose: true);
}
