using Microsoft.Extensions.Logging;
using RouteBridge.Core.Diagnostics;
using RouteBridge.Core.Net;
using RouteBridge.Infrastructure.Diagnostics;
using RouteBridge.Tunnel.Diagnostics;

namespace RouteBridge.App.Services;

/// <summary>
/// One answer to "can this machine host, and what will the other side see?", shared by the host page and the first run so
/// the two can never contradict each other. It caches the last result, collapses concurrent probes into one, and runs off
/// the UI thread — the firewall half shells out to netsh and must never be in the way of a toggle.
/// </summary>
public sealed class HostReadinessMonitor
{
    private readonly IHostReadinessProbe _probe;
    private readonly ILogger<HostReadinessMonitor> _logger;
    private readonly object _gate = new();
    private Task<HostReadiness>? _inFlight;

    public HostReadinessMonitor(IHostReadinessProbe probe, ILogger<HostReadinessMonitor> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    /// <summary>The last result, or <see cref="HostReadiness.Unknown"/> before the first probe.</summary>
    public HostReadiness Last { get; private set; } = HostReadiness.Unknown;

    /// <summary>False until a probe has completed at least once (the UI shows "checking" until then).</summary>
    public bool HasProbed { get; private set; }

    /// <summary>Raised on a background thread after every completed probe.</summary>
    public event Action<HostReadiness>? Updated;

    /// <summary>
    /// The cached answer, or a fresh probe when there is none (or <paramref name="force"/> is set — the VPN can come and
    /// go between making the device available and a session actually starting, which is the second moment we ask).
    /// Never throws.
    /// </summary>
    public Task<HostReadiness> RefreshAsync(bool force, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!force && HasProbed)
            {
                return Task.FromResult(Last);
            }

            return _inFlight ??= ProbeAsync(ct);
        }
    }

    private async Task<HostReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var readiness = await Task.Run(() => _probe.InspectAsync(ct), ct).ConfigureAwait(false);
            Last = readiness;
            HasProbed = true;
            return readiness;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The host readiness probe failed; nothing is claimed about this machine");
            return Last;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }

            try
            {
                Updated?.Invoke(Last);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A readiness handler threw");
            }
        }
    }
}

/// <summary>
/// <see cref="IVpnDetector"/> over track B's <c>HostDiagnostics.DetectVpn()</c>. It exists because Infrastructure cannot
/// reference Tunnel: the App can, so the App is where the two meet. Nothing is re-implemented here — the whole class is the
/// one call — which is the point: there is exactly one set of VPN rules in the client, and it is track B's.
/// </summary>
public sealed class TunnelVpnDetector : IVpnDetector
{
    public VpnDetectionResult Detect() => HostDiagnostics.DetectVpn();
}
