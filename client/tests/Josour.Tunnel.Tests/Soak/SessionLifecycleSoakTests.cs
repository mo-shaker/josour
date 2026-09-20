using System.Diagnostics;
using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Certificates;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Soak;

/// <summary>
/// A soak of <b>the session's lifecycle</b> rather than of traffic: repeated creation and disposal of the system resources the session owns —
/// the session certificate (and its key container on Windows), the listener's socket, the candidate source — because these do not show up in one long-lived
/// tunnel however long it lives. A defect here means a machine that exhausts file descriptors or key containers over a full working day.
/// </summary>
[Trait("Category", "Benchmark")]
public class SessionLifecycleSoakTests
{
    private const double MiB = 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public SessionLifecycleSoakTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A complete cycle: <c>PrepareAsync</c> (a certificate + a listener + candidates) then <c>EndAsync</c> with the full cleanup order.
    /// The default duration is shorter than the tunnel soak's because the cycle here is very fast and the measurement is on the count rather than on the time.
    /// </summary>
    [Fact]
    public async Task RepeatedPrepareAndEnd_DoesNotLeakHandlesOrCertificates()
    {
        var options = SoakOptions.FromEnvironment(defaultMinutes: 5);
        using var recorder = new SoakRecorder("session-lifecycle", options);
        _output.WriteLine($"session lifecycle soak: {options.Duration.TotalMinutes:F1} min of prepare/end cycles");

        var clock = Stopwatch.StartNew();
        var nextSample = options.SampleInterval;
        long cycles = 0;

        // A warm-up: the first cycles pay for assembly loading and one-off allocations, so they are not counted in the baseline.
        for (var i = 0; i < 20; i++) await OneCycleAsync();

        while (clock.Elapsed < options.Duration)
        {
            await OneCycleAsync();
            cycles++;
            if (clock.Elapsed < nextSample) continue;
            nextSample = clock.Elapsed + options.SampleInterval;
            var forced = ProcessProbe.ForcedCollections;
            var (gen0, gen1, gen2) = (GC.CollectionCount(0) - forced.Gen0, GC.CollectionCount(1) - forced.Gen1, GC.CollectionCount(2) - forced.Gen2);
            recorder.Add(new SoakSample(
                clock.Elapsed.TotalSeconds,
                ProcessProbe.Settled(), ProcessProbe.WorkingSet(),
                gen0, gen1, gen2,
                ProcessProbe.ThreadCount(), ProcessProbe.OpenHandles(),
                0, 0, 0, 0, cycles, 0, 0, 0, 0, 0));
        }

        _output.WriteLine($"--- session-lifecycle: {cycles} prepare/end cycles, {recorder.Samples.Count} samples");
        foreach (var (metric, _, text) in recorder.Trends()) _output.WriteLine($"    {metric,-24} {text}");
        recorder.WriteSummary(new Dictionary<string, object?> { ["cycles"] = cycles });

        Assert.True(recorder.Samples.Count >= 4, $"only {recorder.Samples.Count} samples; raise ROUTEBRIDGE_SOAK_MINUTES");
        var handles = Trend.Of(recorder.Samples, s => s.OpenHandles);
        if (handles.Max > 0)
        {
            Assert.True(handles.Growth < 16, $"open handles grew by {handles.Growth:F0} across {cycles} session cycles ({handles.Describe("handles")})");
        }
        var heap = Trend.Of(recorder.Samples, s => s.ManagedHeapBytes);
        Assert.True(heap.Growth < 8 * MiB, $"settled heap grew {heap.Growth / MiB:F1} MiB across {cycles} session cycles ({heap.Describe("MiB", MiB)})");
    }

    private static async Task OneCycleAsync()
    {
        var material = TestMaterial.Create(TunnelRole.Host);
        var session = new Josour.Tunnel.TunnelSession(material, new Josour.Tunnel.TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableUpnp = false,
            CandidateSource = () => new StaticCandidateSource(),
            HostEgress = _ => new FakeEgress(),
        });
        await session.PrepareAsync(CancellationToken.None);
        await session.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);
        Assert.True(session.CertificateDisposed);
    }

    /// <summary>
    /// The certificate alone at large counts: on Windows every creation imports a PFX with <c>UserKeySet</c>, so a key container is created
    /// that <c>Dispose</c> must delete. This test is what will expose it surviving there; on macOS it measures file descriptors
    /// and memory only (see "what remains unproven" in <c>docs/soak-and-fuzz-week6.md</c>).
    /// </summary>
    [Fact]
    public void RepeatedCertificateCreateAndDispose_DoesNotLeak()
    {
        const int cycles = 2000;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        for (var i = 0; i < 50; i++) SessionCertificate.Create(expiresAt).Dispose();

        var handlesBefore = ProcessProbe.OpenHandles();
        var heapBefore = ProcessProbe.Settled();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < cycles; i++)
        {
            var certificate = SessionCertificate.Create(expiresAt);
            Assert.Equal(64, certificate.FingerprintHex.Length);
            certificate.Dispose();
            Assert.True(certificate.IsDisposed);
        }
        var elapsed = clock.Elapsed;
        var heapAfter = ProcessProbe.Settled();
        var handlesAfter = ProcessProbe.OpenHandles();

        _output.WriteLine($"{cycles} certificate create/dispose cycles in {elapsed.TotalSeconds:F1} s ({elapsed.TotalMilliseconds / cycles:F2} ms each); " +
            $"handles {handlesBefore} → {handlesAfter}; settled heap {heapBefore / MiB:F1} → {heapAfter / MiB:F1} MiB; os = {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        if (handlesBefore > 0) Assert.True(handlesAfter - handlesBefore < 16, $"open handles grew by {handlesAfter - handlesBefore} across {cycles} certificates");
        Assert.True(heapAfter - heapBefore < 8 * MiB, $"settled heap grew {(heapAfter - heapBefore) / MiB:F1} MiB across {cycles} certificates");
    }
}
