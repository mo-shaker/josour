using System.Diagnostics;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;
using Josour.Tunnel.Tests.Perf;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Soak;

/// <summary>
/// The soak tests: what only time reveals. They run unattended and produce machine-readable results:
/// <code>
///   export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
///   ROUTEBRIDGE_SOAK_MINUTES=30 ROUTEBRIDGE_SOAK_OUT=/tmp/soak \
///     dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~TunnelSoakTests'
/// </code>
/// Every run writes <c>&lt;name&gt;.jsonl</c> (one sample per line during the run) and <c>&lt;name&gt;.summary.json</c>
/// (the trend table) in <c>ROUTEBRIDGE_SOAK_OUT</c>, and prints the same table in the test output.
///
/// <para>The leak verdict is a trend rather than an instant: the second half's median is compared with the first half's, and the slope per hour is computed.
/// The thresholds are deliberately generous (we do not measure noise), but any real monotonic growth exceeds them in a half-hour run.</para>
/// </summary>
[Trait("Category", "Benchmark")]
public class TunnelSoakTests
{
    private const double MiB = 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public TunnelSoakTests(ITestOutputHelper output) => _output = output;

    // ---------- 1. A tunnel under realistic load ----------

    /// <summary>
    /// One tunnel over a link with an international RTT, under continuous realistic load: streams opened and closed at a steady rate, one long-lived download,
    /// periodic idle gaps, and abrupt terminations (RST) on one in ten. Liveness is enabled (PING/PONG as in the contract).
    /// </summary>
    [Fact]
    public async Task WorkingTunnel_DoesNotGrowOverTime()
    {
        var options = SoakOptions.FromEnvironment();
        using var recorder = new SoakRecorder("working-tunnel", options);
        _output.WriteLine($"soak: {options.Duration.TotalMinutes:F1} min at {options.RttMs:F0} ms RTT, sampling every {options.SampleInterval.TotalSeconds:F0} s" +
            (recorder.JsonlPath is null ? " (no ROUTEBRIDGE_SOAK_OUT: results in this log only)" : $" → {recorder.JsonlPath}"));

        var link = new SimulatedLink(LinkProfile.FromRtt(options.RttMs));
        // Liveness is deliberately enabled: the outstanding PING map and the cancellation registrations on it are the first suspects for a leak.
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

        // After all of that: the counters return to zero and the tunnel still works.
        await Wait.UntilAsync(() => pair.Guest.Stats.OpenStreams == 0 && pair.Host.Stats.OpenStreams == 0, TimeSpan.FromSeconds(30));
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        await AssertStillUsableAsync(pair);
    }

    // ---------- 2. An idle tunnel ----------

    /// <summary>
    /// A connected tunnel with no traffic: nothing runs on it but PING/PONG every twenty seconds. What is required is proof that the liveness loop accumulates nothing
    /// (the PING map, the cancellation registrations on <c>_cts</c>, the timers, the threads) and that the tunnel stays usable afterwards.
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

        // Idle is tighter: nothing is running, so any growth is a leak rather than allocation noise.
        AssertNoLeak(recorder, heapGrowthMiB: 4, handleGrowth: 8, threadGrowth: 8);
        var pings = recorder.Samples.Max(s => Math.Max(s.GuestPendingPings, s.HostPendingPings));
        Assert.True(pings <= 2, $"pending PING map peaked at {pings}; the liveness loop is accumulating entries");

        Assert.False(pair.Guest.IsClosed, "the idle tunnel died during the soak");
        Assert.False(pair.Host.IsClosed, "the idle tunnel died during the soak");
        await AssertStillUsableAsync(pair);
    }

    // ---------- The load ----------

    /// <summary>
    /// The streams' cycle: open, request a realistically sized resource, read, close. One in ten is terminated abruptly mid-transfer
    /// (the equivalent of <c>RST</c>): that is the path where per-stream state would remain if any remained.
    /// And an idle gap of twenty seconds every two minutes: the tunnel goes through real periods of silence in actual use.
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
                var size = 32 * 1024 + n % 8 * 32 * 1024;   // 32 KiB to 256 KiB
                await ResourceProtocol.RequestAsync(stream, size, ct);

                var head = new byte[1];
                await stream.ReadExactlyAsync(head, ct);     // the first byte: this is what the latency measures
                latency.Add(clock.Elapsed.TotalMilliseconds);

                if (n % 10 == 9)
                {
                    // An abrupt termination mid-transfer: no half-close and no draining.
                    onReset();
                    await stream.DisposeAsync();
                }
                else
                {
                    await ResourceProtocol.ReadExactAsync(stream, size - 1, ct);
                    onBytes(size);
                    await ResourceProtocol.RequestAsync(stream, 0, ct);   // a request of length zero = a polite close
                    await ((IHalfClosable)stream).CompleteWritingAsync(ct);
                    await stream.DisposeAsync();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { await Delay(TimeSpan.FromMilliseconds(500), ct); }

            await Delay(TimeSpan.FromMilliseconds(250), ct);   // about four streams a second
        }
    }

    /// <summary>One long-lived download that requests again on the same stream: the longest-lived thing in the session.</summary>
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

    // ---------- Sampling and the verdict ----------

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
            // The collection counters are read before the forced collection and what the probe itself performed is subtracted, otherwise the column would be a measurement
            // of the probe rather than of allocation pressure (on an idle tunnel the probe's collections are all that appears in it).
            var forced = ProcessProbe.ForcedCollections;
            var (gen0, gen1, gen2) = (GC.CollectionCount(0) - forced.Gen0, GC.CollectionCount(1) - forced.Gen1, GC.CollectionCount(2) - forced.Gen2);
            recorder.Add(new SoakSample(
                clock.Elapsed.TotalSeconds,
                // Settled memory after a full collection: without this the graph would be uncollected garbage rather than a leak.
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

    /// <summary>The verdict: the second half's median growing over the first beyond the threshold = a leak. The thresholds are in megabytes and absolute counts.</summary>
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

        // The outstanding PING map: the contract permits one outstanding per side at any moment, so three is a generous ceiling.
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

    /// <summary>After the soak: the tunnel opens a stream and returns the bytes as it did in the first second.</summary>
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
        try { await task; } catch (Exception) { /* the load ends on cancellation */ }
    }
}
