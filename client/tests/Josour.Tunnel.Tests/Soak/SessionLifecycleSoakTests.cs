using System.Diagnostics;
using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Certificates;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Soak;

/// <summary>
/// تحمّل <b>دورة حياة الجلسة</b> لا حركة المرور: إنشاء وتخلص متكرران لما تملكه الجلسة من موارد النظام —
/// شهادة الجلسة (وحاوية مفتاحها على Windows)، مقبس المستمع، مصدر المرشحين — لأن هذه لا تظهر في نفق واحد
/// طويل العمر مهما طال. عيب هنا يعني جهازًا يستنزف واصفات ملفات أو حاويات مفاتيح على مدى يوم عمل كامل.
/// </summary>
[Trait("Category", "Benchmark")]
public class SessionLifecycleSoakTests
{
    private const double MiB = 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public SessionLifecycleSoakTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// دورة كاملة: <c>PrepareAsync</c> (شهادة + مستمع + مرشحون) ثم <c>EndAsync</c> بترتيب التنظيف كاملًا.
    /// المدة الافتراضية أقصر من تحمّل النفق لأن الدورة هنا سريعة جدًا والقياس على العدد لا على الزمن.
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

        // إحماء: أول دورات تدفع تحميل التجميعات وتخصيصات لمرة واحدة، فلا تُحسب في خط الأساس.
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
    /// الشهادة وحدها بأعداد كبيرة: على Windows كل إنشاء يستورد PFX بـ <c>UserKeySet</c> فتُنشأ حاوية مفتاح
    /// يجب أن يحذفها <c>Dispose</c>. هذا الاختبار هو ما سيكشف بقاءها هناك؛ على macOS يقيس واصفات الملفات
    /// والذاكرة فقط (انظر «ما يبقى غير مثبت» في <c>docs/soak-and-fuzz-week6.md</c>).
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
