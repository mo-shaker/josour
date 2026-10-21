using System.Diagnostics;
using Josour.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.SystemProxy;

/// <summary>
/// What keeps the settings recoverable when this process cannot: a guardian process that restores them if the app dies,
/// and a restore at the next login. Implemented by the application (it knows its own executable); none in the tests.
/// </summary>
public interface ISystemProxySafetyNet
{
    /// <summary>Called once the marker is on disk and before the settings change.</summary>
    void Arm(SystemProxyRunMarker marker);

    /// <summary>Called once the settings are back and the marker is gone.</summary>
    void Disarm();
}

public sealed class NoSafetyNet : ISystemProxySafetyNet
{
    public static NoSafetyNet Instance { get; } = new();
    public void Arm(SystemProxyRunMarker marker) { }
    public void Disarm() { }
}

/// <summary>The session's view of the system proxy: point it at the local proxy, and put it back.</summary>
public interface ISystemProxyGuard
{
    bool IsSupported { get; }

    /// <summary>Is a session's change in force (a marker on disk)?</summary>
    bool IsApplied { get; }

    /// <summary>
    /// Points the system at <c>127.0.0.1:port</c>, in this order: settings read, marker written, safety net armed, settings
    /// changed, settings read back. Any failure puts back what was changed and throws <see cref="SystemProxyException"/>.
    /// </summary>
    Task ApplyAsync(int port, IReadOnlyList<string> extraBypass, CancellationToken ct);

    /// <summary>
    /// Puts back the settings recorded in the marker, if there is one, and removes it. Idempotent; never throws. False when
    /// there was nothing to restore or the restore failed (the marker then stays for the next attempt).
    /// </summary>
    Task<bool> RestoreAsync(CancellationToken ct);
}

/// <summary>
/// The crash-safe sequence around <see cref="ISystemProxyController"/> (ADR-0015 point 2). The marker goes to disk before
/// anything changes and leaves only after the settings are back, so every path out — a normal end, a crash caught by the
/// guardian, a power cut caught at the next start or login — finds what it needs to restore.
/// </summary>
public sealed class SystemProxyGuard : ISystemProxyGuard
{
    private readonly ISystemProxyController _controller;
    private readonly ISystemProxyMarkerStore _markers;
    private readonly ISystemProxySafetyNet _safetyNet;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SystemProxyGuard(
        ISystemProxyController controller,
        ISystemProxyMarkerStore markers,
        ISystemProxySafetyNet? safetyNet = null,
        ILogger<SystemProxyGuard>? logger = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _safetyNet = safetyNet ?? NoSafetyNet.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool IsSupported => _controller.IsSupported;

    public bool IsApplied => _markers.Read() is not null;

    public async Task ApplyAsync(int port, IReadOnlyList<string> extraBypass, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extraBypass);
        if (!_controller.IsSupported)
        {
            throw new SystemProxyException("unsupported", "this operating system's proxy settings cannot be changed by Josour");
        }

        // A marker left by an earlier run means the settings on disk are not the user's own: put those back first,
        // or the snapshot below would record our own leftovers as "how it was".
        if (_markers.Read() is not null && !await RestoreAsync(ct).ConfigureAwait(false))
        {
            throw new SystemProxyException("restore_failed", "the settings left by an earlier session could not be put back");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = new SystemProxyTarget(port, extraBypass);
            var snapshot = await _controller.CaptureAsync(ct).ConfigureAwait(false);
            var marker = new SystemProxyRunMarker(Environment.ProcessId, ProcessStart(), port, snapshot, DateTimeOffset.UtcNow);
            if (!_markers.Write(marker))
            {
                throw new SystemProxyException("marker_failed", "the note needed to undo the change could not be saved, so nothing was changed");
            }

            OwnProxyPorts.Add(port);
            try
            {
                _safetyNet.Arm(marker);
                await _controller.ApplyAsync(target, ct).ConfigureAwait(false);
                if (!await _controller.IsAppliedAsync(target, ct).ConfigureAwait(false))
                {
                    throw new SystemProxyException("not_applied", "the system proxy did not read back as set");
                }
                _logger.LogInformation("System proxy pointed at 127.0.0.1:{Port}", port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pointing the system proxy at 127.0.0.1:{Port} failed; putting the settings back", port);
                await RestoreCoreAsync(marker, CancellationToken.None).ConfigureAwait(false);
                if (ex is OperationCanceledException) throw;
                throw ex as SystemProxyException ?? new SystemProxyException("apply_failed", ex.Message, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var marker = _markers.Read();
            return marker is not null && await RestoreCoreAsync(marker, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RestoreCoreAsync(SystemProxyRunMarker marker, CancellationToken ct)
    {
        try
        {
            await _controller.RestoreAsync(marker.Snapshot, marker.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The marker stays: the next start, the next login or "Repair" tries again.
            _logger.LogError(ex, "The system proxy settings could not be put back (port {Port})", marker.Port);
            return false;
        }

        _markers.Clear();
        OwnProxyPorts.Remove(marker.Port);
        try { _safetyNet.Disarm(); } catch (Exception ex) { _logger.LogWarning(ex, "Disarming the system-proxy safety net failed"); }
        _logger.LogInformation("System proxy settings put back (they pointed at 127.0.0.1:{Port})", marker.Port);
        return true;
    }

    private static DateTimeOffset ProcessStart()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// Head-less use (the Spike tool): nothing is ever pointed at the local proxy. Whatever the caller sends through
/// <c>127.0.0.1:port</c> itself is what exercises it.
/// </summary>
public sealed class HeadlessSystemProxyGuard : ISystemProxyGuard
{
    public static HeadlessSystemProxyGuard Instance { get; } = new();
    public bool IsSupported => true;
    public bool IsApplied => false;
    public Task ApplyAsync(int port, IReadOnlyList<string> extraBypass, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> RestoreAsync(CancellationToken ct) => Task.FromResult(false);
}

/// <summary>The controller for this operating system.</summary>
public static class SystemProxyControllers
{
    /// <summary>Can this operating system's proxy be pointed at us (and so can this machine be a guest)?</summary>
    public static bool SupportedOnThisPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static ISystemProxyController ForCurrentPlatform(ILoggerFactory? loggers = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsSystemProxyController();
        if (OperatingSystem.IsMacOS()) return new MacSystemProxyController(logger: loggers?.CreateLogger<MacSystemProxyController>());
        return UnsupportedSystemProxyController.Instance;
    }
}
