using System.Diagnostics;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;
using Josour.Tunnel.Tests.Perf;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Soak;

/// <summary>
/// اختبارات التحمّل: ما لا يكشفه إلا الوقت. تُشغَّل بلا مراقبة وتخرج بنتائج آلية:
/// <code>
///   export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
///   ROUTEBRIDGE_SOAK_MINUTES=30 ROUTEBRIDGE_SOAK_OUT=/tmp/soak \
///     dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~TunnelSoakTests'
/// </code>
/// كل تشغيل يكتب <c>&lt;الاسم&gt;.jsonl</c> (عيّنة في كل سطر أثناء التشغيل) و<c>&lt;الاسم&gt;.summary.json</c>
/// (جدول الاتجاهات) في <c>ROUTEBRIDGE_SOAK_OUT</c>، ويطبع الجدول نفسه في مخرجات الاختبار.
///
/// <para>الحكم على التسريب اتجاهي لا لحظي: يُقارَن وسيط النصف الثاني بوسيط النصف الأول، ويُحسب الميل بالساعة.
/// الحدود سخية عمدًا (لا نقيس الضجيج) لكن أي نمو رتيب حقيقي يتجاوزها في تشغيل نصف ساعة.</para>
/// </summary>
[Trait("Category", "Benchmark")]
public class TunnelSoakTests
{
    private const double MiB = 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public TunnelSoakTests(ITestOutputHelper output) => _output = output;

    // ---------- 1. نفق تحت حمل واقعي ----------

    /// <summary>
    /// نفق واحد فوق وصلة بـ RTT دولي، بحمل واقعي متصل: streams تُفتح وتُغلق بمعدل ثابت، وتنزيل واحد طويل العمر،
    /// وفجوات خمول دورية، وإنهاءات مفاجئة (RST) على واحد من كل عشرة. الحيوية مفعّلة (PING/PONG كما في العقد).
    /// </summary>
    [Fact]
    public async Task WorkingTunnel_DoesNotGrowOverTime()
    {
        var options = SoakOptions.FromEnvironment();
        using var recorder = new SoakRecorder("working-tunnel", options);
        _output.WriteLine($"soak: {options.Duration.TotalMinutes:F1} min at {options.RttMs:F0} ms RTT, sampling every {options.SampleInterval.TotalSeconds:F0} s" +
            (recorder.JsonlPath is null ? " (no ROUTEBRIDGE_SOAK_OUT: results in this log only)" : $" → {recorder.JsonlPath}"));

        var link = new SimulatedLink(LinkProfile.FromRtt(options.RttMs));
        // الحيوية مفعّلة عمدًا: خريطة الـ PING المعلّقة وتسجيلات الإلغاء عليها هي أول ما يُتَّهم بالتسريب.
        var muxOptions = new MuxOptions { EnableLiveness = true, ReceiveWindow = MuxWindow.RegionalWindow, OpenTimeout = TimeSpan.FromSeconds(60) };
        await using var pair = await MuxPair.CreateAsync(
            guestOptions: muxOptions, hostOptions: muxOptions,
            wrapGuest: link.WrapA, wrapHost: link.WrapB, handshakeTimeout: TimeSpan.FromSeconds(60));

        var targets = 0;
        pair.Host.OpenRequested = (_, _) => { Interlocked.Increment(ref targets); return Task.FromResult(MuxOpenDecision.Ok(new ResourceTarget())); };

        var latency = new LatencyWindow();
        long opened = 0, reset = 0, bytes = 0;
        using var stop = new CancellationTokenSource();

        var churn = Task.Run(() => ChurnAsync(pair.Guest, latency, () => Interlocked.Increment(ref opened), () => Interlocked.Increment(ref reset),
            n => Interlocked.Add(ref bytes, n), stop.Token));
        var download = Task.Run(() => LongDownloadAsync(pair.Guest, n => Interlocked.Add(ref bytes, n), stop.Token));

        await SampleUntilAsync(recorder, options, pair, latency, () => (Volatile.Read(ref opened), Volatile.Read(ref reset), Volatile.Read(ref bytes)), stop.Token);

        stop.Cancel();
        await Task.WhenAll(SafeAsync(churn), SafeAsync(download));

        Report(recorder, $"opened {Volatile.Read(ref opened)} streams ({Volatile.Read(ref reset)} reset mid-transfer), moved {Volatile.Read(ref bytes) / MiB:F0} MiB");
        recorder.WriteSummary(new Dictionary<string, object?>
        {
            ["streams_opened"] = Volatile.Read(ref opened),
            ["streams_reset"] = Volatile.Read(ref reset),
            ["bytes_moved"] = Volatile.Read(ref bytes),
            ["host_open_decisions"] = Volatile.Read(ref targets),
        });

        AssertNoLeak(recorder, heapGrowthMiB: 24, handleGrowth: 48, threadGrowth: 24);
        AssertLatencyStable(recorder, factor: 3.0);

        // بعد كل هذا: العدّادات تعود إلى الصفر والنفق ما زال يعمل.
        await Wait.UntilAsync(() => pair.Guest.Stats.OpenStreams == 0 && pair.Host.Stats.OpenStreams == 0, TimeSpan.FromSeconds(30));
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        await AssertStillUsableAsync(pair);
    }

    // ---------- 2. نفق خامل ----------

    /// <summary>
    /// نفق متصل بلا حركة: لا يجري فيه إلا PING/PONG كل عشرين ثانية. المطلوب إثبات أن حلقة الحيوية لا تراكم شيئًا
    /// (خريطة الـ PING، تسجيلات الإلغاء على <c>_cts</c>، المؤقتات، الخيوط) وأن النفق يبقى صالحًا بعدها.
    /// </summary>
    [Fact]
    public async Task IdleTunnel_AccumulatesNothing_AndStaysUsable()
    {
        var options = SoakOptions.FromEnvironment();
        using var recorder = new SoakRecorder("idle-tunnel", options);
        _output.WriteLine($"idle soak: {options.Duration.TotalMinutes:F1} min at {options.RttMs:F0} ms RTT, liveness on (PING every 20 s)");

        var link = new SimulatedLink(LinkProfile.FromRtt(options.RttMs));
        var muxOptions = new MuxOptions { EnableLiveness = true, ReceiveWindow = MuxWindow.RegionalWindow };
        await using var pair = await MuxPair.CreateAsync(
            guestOptions: muxOptions, hostOptions: muxOptions,
            wrapGuest: link.WrapA, wrapHost: link.WrapB, handshakeTimeout: TimeSpan.FromSeconds(60));
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new ResourceTarget()));

        var latency = new LatencyWindow();
        using var stop = new CancellationTokenSource();
        await SampleUntilAsync(recorder, options, pair, latency, () => (0L, 0L, 0L), stop.Token);

        Report(recorder, "no traffic; PING/PONG only");
        recorder.WriteSummary(new Dictionary<string, object?> { ["workload"] = "idle" });

        // الخمول أضيق: لا شيء يجري، فأي نمو تسريب لا ضجيج تخصيص.
        AssertNoLeak(recorder, heapGrowthMiB: 4, handleGrowth: 8, threadGrowth: 8);
        var pings = recorder.Samples.Max(s => Math.Max(s.GuestPendingPings, s.HostPendingPings));
        Assert.True(pings <= 2, $"pending PING map peaked at {pings}; the liveness loop is accumulating entries");

        Assert.False(pair.Guest.IsClosed, "the idle tunnel died during the soak");
        Assert.False(pair.Host.IsClosed, "the idle tunnel died during the soak");
        await AssertStillUsableAsync(pair);
    }

    // ---------- الحمل ----------

    /// <summary>
    /// دورة الـ streams: فتح، طلب مورد بحجم واقعي، قراءة، إغلاق. واحد من كل عشرة يُنهى فجأة في منتصف النقل
    /// (نظير <c>RST</c>): هذا هو المسار الذي تبقى فيه حالة لكل stream إن كان هناك ما يبقى.
    /// وفجوة خمول عشرين ثانية كل دقيقتين: النفق يمر بفترات صمت حقيقية في الاستعمال الفعلي.
    /// </summary>
    private static async Task ChurnAsync(NerdbankMux guest, LatencyWindow latency, Action onOpen, Action onReset, Action<long> onBytes, CancellationToken ct)
    {
        var index = 0;
        var sinceGap = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            if (sinceGap.Elapsed > TimeSpan.FromMinutes(2))
            {
                await Delay(TimeSpan.FromSeconds(20), ct);
                sinceGap.Restart();
                continue;
            }

            var n = index++;
            try
            {
                var clock = Stopwatch.StartNew();
                var open = await guest.OpenStreamAsync($"churn{n % 32}.test", 443, ct);
                if (!open.IsOpen)
                {
                    await Delay(TimeSpan.FromMilliseconds(250), ct);
                    continue;
                }

                onOpen();
                var stream = open.Stream!;
                var size = 32 * 1024 + n % 8 * 32 * 1024;   // 32 KiB إلى 256 KiB
                await ResourceProtocol.RequestAsync(stream, size, ct);

                var head = new byte[1];
                await stream.ReadExactlyAsync(head, ct);     // أول بايت: هذا ما يقيسه زمن الاستجابة
                latency.Add(clock.Elapsed.TotalMilliseconds);

                if (n % 10 == 9)
                {
                    // إنهاء مفاجئ في منتصف النقل: لا إغلاق نصفي ولا استنزاف.
                    onReset();
                    await stream.DisposeAsync();
                }
                else
                {
                    await ResourceProtocol.ReadExactAsync(stream, size - 1, ct);
                    onBytes(size);
                    await ResourceProtocol.RequestAsync(stream, 0, ct);   // طلب بطول صفر = إغلاق مهذب
                    await ((IHalfClosable)stream).CompleteWritingAsync(ct);
                    await stream.DisposeAsync();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Delay(TimeSpan.FromMilliseconds(500), ct); }

            await Delay(TimeSpan.FromMilliseconds(250), ct);   // نحو أربعة streams في الثانية
        }
    }

    /// <summary>تنزيل واحد طويل العمر يعيد الطلب على الـ stream نفسه: أطول ما يعيش في الجلسة.</summary>
    private static async Task LongDownloadAsync(NerdbankMux guest, Action<long> onBytes, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var open = await guest.OpenStreamAsync("download.test", 443, ct);
                if (!open.IsOpen) { await Delay(TimeSpan.FromSeconds(1), ct); continue; }
                await using var stream = open.Stream!;
                while (!ct.IsCancellationRequested)
                {
                    const long chunk = 2 * 1024 * 1024;
                    await ResourceProtocol.RequestAsync(stream, chunk, ct);
                    await ResourceProtocol.ReadExactAsync(stream, chunk, ct);
                    onBytes(chunk);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Delay(TimeSpan.FromSeconds(1), ct); }
        }
    }

    // ---------- أخذ العيّنات والحكم ----------

    private static async Task SampleUntilAsync(
        SoakRecorder recorder, SoakOptions options, MuxPair pair, LatencyWindow latency,
        Func<(long Opened, long Reset, long Bytes)> counters, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < options.Duration && !ct.IsCancellationRequested)
        {
            await Delay(options.SampleInterval, ct);
            var (p50, p95, max) = latency.DrainPercentiles();
            var (opened, reset, bytes) = counters();
            // عدّادات الجمع تُقرأ قبل الجمع القسري ويُطرح منها ما أجراه المقياس نفسه، وإلا كان العمود قياسًا
            // للمقياس لا لضغط التخصيص (على نفق خامل تكون عمليات المقياس هي كل ما يظهر فيه).
            var forced = ProcessProbe.ForcedCollections;
            var (gen0, gen1, gen2) = (GC.CollectionCount(0) - forced.Gen0, GC.CollectionCount(1) - forced.Gen1, GC.CollectionCount(2) - forced.Gen2);
            recorder.Add(new SoakSample(
                clock.Elapsed.TotalSeconds,
                // ذاكرة مستقرة بعد جمع كامل: بلا هذا يكون الرسم قمامةً لم تُجمع بعد لا تسريبًا.
                ProcessProbe.Settled(),
                ProcessProbe.WorkingSet(),
                gen0, gen1, gen2,
                ProcessProbe.ThreadCount(), ProcessProbe.OpenHandles(),
                pair.Guest.Stats.OpenStreams, pair.Host.Stats.OpenStreams,
                pair.Guest.PendingPings, pair.Host.PendingPings,
                opened, reset, bytes,
                p50, p95, max));
        }
    }

    private void Report(SoakRecorder recorder, string workload)
    {
        _output.WriteLine($"--- {recorder.RunName}: {recorder.Samples.Count} samples over {recorder.Options.Duration.TotalMinutes:F1} min; {workload}");
        foreach (var (metric, _, text) in recorder.Trends()) _output.WriteLine($"    {metric,-24} {text}");
        if (recorder.JsonlPath is not null) _output.WriteLine($"    samples: {recorder.JsonlPath}");
        if (recorder.SummaryPath is not null) _output.WriteLine($"    summary: {recorder.SummaryPath}");
    }

    /// <summary>الحكم: نمو وسيط النصف الثاني عن الأول فوق الحد = تسريب. الحدود بالميغابايت والعدد المطلق.</summary>
    private static void AssertNoLeak(SoakRecorder recorder, double heapGrowthMiB, int handleGrowth, int threadGrowth)
    {
        Assert.True(recorder.Samples.Count >= 4, $"only {recorder.Samples.Count} samples; a trend needs at least four (raise ROUTEBRIDGE_SOAK_MINUTES)");

        var heap = Trend.Of(recorder.Samples, s => s.ManagedHeapBytes);
        Assert.True(heap.Growth < heapGrowthMiB * MiB,
            $"settled managed heap grew {heap.Growth / MiB:F1} MiB between halves ({heap.Describe("MiB", MiB)})");

        var handles = Trend.Of(recorder.Samples, s => s.OpenHandles);
        if (handles.Max > 0)
        {
            Assert.True(handles.Growth < handleGrowth,
                $"open handles grew by {handles.Growth:F0} between halves ({handles.Describe("handles")})");
        }

        var threads = Trend.Of(recorder.Samples, s => s.ThreadCount);
        Assert.True(threads.Growth < threadGrowth, $"thread count grew by {threads.Growth:F0} between halves ({threads.Describe("threads")})");

        // خريطة الـ PING المعلّقة: العقد يسمح بواحد معلّق لكل طرف في كل لحظة، فثلاثة سقف سخي.
        var pending = recorder.Samples.Max(s => Math.Max(s.GuestPendingPings, s.HostPendingPings));
        Assert.True(pending <= 3, $"pending PING map peaked at {pending}; entries are outliving their round trip");
    }

    private static void AssertLatencyStable(SoakRecorder recorder, double factor)
    {
        var latency = Trend.Of(recorder.Samples.Where(s => s.LatencyP50Ms > 0).ToList(), s => s.LatencyP50Ms);
        if (latency.Count < 4) return;
        Assert.True(latency.SecondHalfMedian <= Math.Max(latency.FirstHalfMedian * factor, 50),
            $"per-stream latency drifted: {latency.Describe("ms")}");
    }

    /// <summary>بعد التحمّل: النفق يفتح stream ويعيد البايتات كما في أول ثانية.</summary>
    private static async Task AssertStillUsableAsync(MuxPair pair)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var open = await pair.Guest.OpenStreamAsync("after-soak.test", 443, cts.Token);
        Assert.True(open.IsOpen, $"the tunnel could not open a stream after the soak: {open.Reason}");
        await using var stream = open.Stream!;
        await ResourceProtocol.RequestAsync(stream, 64 * 1024, cts.Token);
        await ResourceProtocol.ReadExactAsync(stream, 64 * 1024, cts.Token);

        var rtt = await pair.Guest.PingAsync(cts.Token);
        Assert.InRange(rtt.TotalMilliseconds, 0, 10_000);
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    private static async Task SafeAsync(Task task)
    {
        try { await task; } catch (Exception) { /* الحمل ينتهي بالإلغاء */ }
    }
}
