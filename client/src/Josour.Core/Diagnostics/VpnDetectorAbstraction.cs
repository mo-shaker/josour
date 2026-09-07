using Josour.Core.Net;

namespace Josour.Core.Diagnostics;

/// <summary>
/// The seam through which the app asks "is this machine behind a VPN?". The rules themselves belong to track B
/// (<see cref="VpnDetector"/> in <c>Josour.Core.Net</c>); this interface exists only so that the layers that ACT on the
/// answer — the host's warning, <c>hello.diagnostics</c> — can be tested with a scripted result instead of the real adapters
/// of whatever machine the tests happen to run on.
/// <para>
/// Callers must warn on <see cref="VpnDetectionResult.ShouldWarn"/> (high confidence: a VPN adapter that actually holds the
/// outbound route) and never on <see cref="VpnDetectionResult.IsVpn"/> alone — a merely present VPN adapter that carries no
/// traffic would make the warning permanent and therefore worthless.
/// </para>
/// </summary>
public interface IVpnDetector
{
    /// <summary>Never throws: an inconclusive machine comes back as <see cref="VpnDetectionResult.NotDetected"/>.</summary>
    VpnDetectionResult Detect();
}

/// <summary>
/// <see cref="IVpnDetector"/> over the real network adapters. The app substitutes track B's
/// <c>Josour.Tunnel.Diagnostics.HostDiagnostics.DetectVpn()</c>, which is this same detector plus that layer's own
/// guard; this class is the fallback for layers that cannot reference Tunnel (Infrastructure) and for tests.
/// </summary>
public sealed class SystemVpnDetector : IVpnDetector
{
    public static SystemVpnDetector Instance { get; } = new();

    public VpnDetectionResult Detect()
    {
        try
        {
            return VpnDetector.Detect();
        }
        catch (Exception)
        {
            return VpnDetectionResult.NotDetected;
        }
    }
}
