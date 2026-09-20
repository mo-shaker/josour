using System.Collections.Concurrent;
using System.Diagnostics;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>
/// Week five's numbers: the same ADR-0006 stack (TLS -> NerdbankMux) but over a <see cref="SimulatedLink"/> with an international RTT
/// instead of loopback's 0 ms. The output is copied into <c>docs/performance-week5.md</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public class WanBenchmarks
{
    private const long MiB = 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private readonly ITestOutputHelper _output;

    public WanBenchmarks(ITestOutputHelper output) => _output = output;

    // ---------- 1) The per-stream throughput ceiling = the window ÷ the RTT ----------

    [Theory]
    [InlineData(50, 1)]
    [InlineData(150, 1)]
    [InlineData(300, 1)]
    [InlineData(150, 4)]
    [InlineData(300, 4)]
    public async Task PerStreamThroughput_IsBoundedByWindowOverRtt(int rttMs, int windowMiB)
    {
        var window = windowMiB * (int)MiB;
        await using var wan = await WanPair.CreateAsync(LinkProfile.FromRtt(rttMs), window);
        var measuredRtt = await wan.MeasureRttAsync();
        var ceiling = WanPair.CeilingBytesPerSecond(window, measuredRtt);

        // The time to open one stream (OPEN's cost in RTTs; the browser pays it for every new CONNECT).
        wan.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new TestTarget(produce: 0)));
        var openClock = Perf.Start();
        var warm = await wan.Guest.OpenStreamAsync("warm.test", 443, CancellationToken.None);
        var openElapsed = openClock.Elapsed;
        Assert.True(warm.IsOpen);
        await warm.Stream!.DisposeAsync();

        var size = (long)Math.Clamp(ceiling * 4, 8 * Perf.MB, 64 * Perf.MB);
        var target = new TestTarget(produce: size);
        wan.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));
        var open = await wan.Guest.OpenStreamAsync("down.test", 443, CancellationToken.None);
        Assert.True(open.IsOpen);
        await using var stream = open.Stream!;

        var clock = Perf.Start();
        var read = await StreamIo.ReadToEndAsync(stream).WaitAsync(Limit);
        var elapsed = clock.Elapsed;
        Assert.Equal(size, read);

        var actual = read / elapsed.TotalSeconds;
        _output.WriteLine(
            $"[throughput] nominal RTT {rttMs} ms | measured RTT {measuredRtt.TotalMilliseconds:F1} ms | TLS {wan.Pair.TlsVersion} | window {windowMiB} MiB | " +
            $"open {openElapsed.TotalMilliseconds:F0} ms ({openElapsed.TotalMilliseconds / measuredRtt.TotalMilliseconds:F1} RTT) | " +
            $"{read / Perf.MB:F0} MB in {elapsed.TotalMilliseconds:F0} ms = {Perf.Rate(read, elapsed)} | " +
            $"ceiling window/RTT = {ceiling / Perf.MB:F2} MB/s ({ceiling * 8 / Perf.MB:F1} Mbit/s) | " +
            $"efficiency {actual / ceiling:P0}");

        // Half the ceiling is a conservative floor: it proves the ceiling is the window/RTT and nothing else.
        Assert.True(actual >= ceiling * 0.5, $"only {actual / Perf.MB:F2} MB/s of a {ceiling / Perf.MB:F2} MB/s ceiling");
        // and that it is not exceeded beyond a reasonable margin (measurement error + the link's buffer).
        Assert.True(actual <= ceiling * 1.6, $"{actual / Perf.MB:F2} MB/s exceeds the window/RTT ceiling {ceiling / Perf.MB:F2} MB/s");
    }

    // ---------- 1b) The ceiling after the amendment: the window derived from the RTT instead of a fixed 1 MiB ----------

    /// <summary>
    /// The same measurement as section 3 but with the window chosen by <see cref="MuxWindow.ForRoundTrip"/> as the session chooses it in production.
    /// The purpose: to prove the single-download ceiling is no longer 56 Mbit/s at 150 ms nor 28 at 300, while printing the memory ceiling
    /// that bought it. The numbers are copied into docs/performance-week5.md, the "after the amendment" section.
    /// </summary>
    [Theory]
    [InlineData(50, 1, 256, 0.9)]
    [InlineData(150, 2, 128, 1.8)]
    [InlineData(300, 4, 64, 3.5)]
    public async Task PerStreamThroughput_WithRttDerivedWindow_ClearsTheSingleDownloadCeiling(int rttMs, int expectedMiB, int expectedStreams, double minGain)
    {
        var derived = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs));
        Assert.Equal(expectedMiB * (int)MiB, derived.ReceiveWindow);
        Assert.Equal(expectedStreams, derived.MaxConcurrentStreams);

        await using var wan = await WanPair.CreateAsync(LinkProfile.FromRtt(rttMs), derived.ReceiveWindow);
        var measuredRtt = await wan.MeasureRttAsync();
        var ceiling = WanPair.CeilingBytesPerSecond(derived.ReceiveWindow, measuredRtt);
        var before = WanPair.CeilingBytesPerSecond(MuxWindow.NearWindow, measuredRtt); // the fixed 1 MiB before the amendment

        var size = (long)Math.Clamp(ceiling * 4, 8 * Perf.MB, 96 * Perf.MB);
        var target = new TestTarget(produce: size);
        wan.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));
        var open = await wan.Guest.OpenStreamAsync("download.test", 443, CancellationToken.None);
        Assert.True(open.IsOpen);
        await using var stream = open.Stream!;

        var clock = Perf.Start();
        var read = await StreamIo.ReadToEndAsync(stream).WaitAsync(Limit);
        var elapsed = clock.Elapsed;
        Assert.Equal(size, read);

        var actual = read / elapsed.TotalSeconds;
        _output.WriteLine(
            $"[derived] nominal RTT {rttMs} ms | measured RTT {measuredRtt.TotalMilliseconds:F1} ms | TLS {wan.Pair.TlsVersion} | " +
            $"derived window {derived.ReceiveWindow / MiB} MiB x {derived.MaxConcurrentStreams} streams " +
            $"(worst-case {derived.WorstCaseBytes / MiB} MiB) | " +
            $"{read / Perf.MB:F0} MB in {elapsed.TotalMilliseconds:F0} ms = {Perf.Rate(read, elapsed)} | " +
            $"ceiling {ceiling * 8 / Perf.MB:F1} Mbit/s | efficiency {actual / ceiling:P0} | " +
            $"before the amendment (1 MiB) {before * 8 / Perf.MB:F1} Mbit/s => x{actual / before:F1}");

        // The reason the contract was amended: a single download above 100 Mbit/s up to 300 ms (it was 56 and 28).
        Assert.True(actual * 8 / Perf.MB >= 100, $"single download capped at {actual * 8 / Perf.MB:F1} Mbit/s");
        // And the gain is real rather than theoretical: the larger window translates into throughput at the expected ratio.
        Assert.True(actual / before >= minGain, $"only x{actual / before:F1} over the 1 MiB ceiling (expected x{minGain})");
        Assert.True(actual >= ceiling * 0.8, $"only {actual / Perf.MB:F2} MB/s of a {ceiling / Perf.MB:F2} MB/s ceiling");
        Assert.True(actual <= ceiling * 1.6, $"{actual / Perf.MB:F2} MB/s exceeds the window/RTT ceiling {ceiling / Perf.MB:F2} MB/s");
        // And the memory ceiling has not moved: the window x the limit = 256 MiB in every band.
        Assert.Equal(MuxWindow.MemoryBudget, derived.WorstCaseBytes);
    }

    // ---------- 2) A heavy page: a document + 80 resources over 30 concurrent paths ----------

    [Theory]
    [InlineData(50, 0)]
    [InlineData(150, 0)]
    [InlineData(300, 0)]
    [InlineData(150, 50)]
    [InlineData(150, 10)]
    public async Task HeavyPageLoad_TunnelVersusDirect(int rttMs, int megabitsPerSecond)
    {
        const int lanes = 30;
        var profile = LinkProfile.FromRtt(rttMs, megabitsPerSecond);
        var lanePlans = PageProfile.Split(lanes);

        // (a) Through the tunnel: a stream per path, reused for its resources in sequence (the equivalent of keep-alive).
        // The clock stops at the last resource byte, not after the connections are torn down (the page load time as the user sees it).
        TimeSpan tunnelElapsed;
        TimeSpan tunnelDocument;
        TimeSpan tunnelFirstOpen;
        TimeSpan tunnelWarmRoundTrip;
        await using (var wan = await WanPair.CreateAsync(profile))
        {
            // The mux owns the destination and disposes of it when the channel closes.
            wan.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new ResourceTarget()));

            var clock = Perf.Start();
            var docOpen = await wan.Guest.OpenStreamAsync("doc.test", 443, CancellationToken.None);
            tunnelFirstOpen = clock.Elapsed;
            Assert.True(docOpen.IsOpen);
            var doc = docOpen.Stream!;
            await ResourceProtocol.RequestAsync(doc, PageProfile.DocumentBytes, CancellationToken.None);
            await ResourceProtocol.ReadExactAsync(doc, PageProfile.DocumentBytes, CancellationToken.None);
            tunnelDocument = clock.Elapsed;

            var laneStreams = new Stream[lanes];
            await Task.WhenAll(lanePlans.Select(async (plan, index) =>
            {
                var open = await wan.Guest.OpenStreamAsync($"lane{index}.test", 443, CancellationToken.None);
                Assert.True(open.IsOpen);
                laneStreams[index] = open.Stream!;
                foreach (var size in plan)
                {
                    await ResourceProtocol.RequestAsync(laneStreams[index], size, CancellationToken.None);
                    await ResourceProtocol.ReadExactAsync(laneStreams[index], size, CancellationToken.None);
                }
            })).WaitAsync(Limit);
            tunnelElapsed = clock.Elapsed;

            // Outside the clock: a round trip on a warm stream with a trivial payload, which separates the request/reply cost from the open and the size.
            var warm = Perf.Start();
            for (var i = 0; i < 5; i++)
            {
                await ResourceProtocol.RequestAsync(doc, 1024, CancellationToken.None);
                await ResourceProtocol.ReadExactAsync(doc, 1024, CancellationToken.None);
            }
            tunnelWarmRoundTrip = warm.Elapsed / 5;

            await doc.DisposeAsync();
            foreach (var stream in laneStreams) await stream.DisposeAsync();
        }

        // (b) Directly: the same link and the same bottleneck, a TCP connection per path (paying a handshake RTT) and no mux.
        await using var direct = new DirectLinkFactory(profile);
        var directClock = Perf.Start();
        var docLink = await direct.ConnectAsync(CancellationToken.None);
        await ResourceProtocol.RequestAsync(docLink, PageProfile.DocumentBytes, CancellationToken.None);
        await ResourceProtocol.ReadExactAsync(docLink, PageProfile.DocumentBytes, CancellationToken.None);
        var directDocument = directClock.Elapsed;

        await Task.WhenAll(lanePlans.Select(async plan =>
        {
            var link = await direct.ConnectAsync(CancellationToken.None);
            foreach (var size in plan)
            {
                await ResourceProtocol.RequestAsync(link, size, CancellationToken.None);
                await ResourceProtocol.ReadExactAsync(link, size, CancellationToken.None);
            }
        })).WaitAsync(Limit);
        var directElapsed = directClock.Elapsed;

        var overhead = tunnelElapsed.TotalMilliseconds / directElapsed.TotalMilliseconds - 1;
        _output.WriteLine(
            $"[page] {profile} | {PageProfile.TotalBytes / 1024} KiB over 1 + {PageProfile.SubResources.Count} resources on {lanes} lanes | " +
            $"tunnel first OPEN {tunnelFirstOpen.TotalMilliseconds:F0} ms ({tunnelFirstOpen.TotalMilliseconds / rttMs:F2} RTT), " +
            $"doc {tunnelDocument.TotalMilliseconds:F0} ms, warm round-trip {tunnelWarmRoundTrip.TotalMilliseconds:F0} ms ({tunnelWarmRoundTrip.TotalMilliseconds / rttMs:F2} RTT), " +
            $"full {tunnelElapsed.TotalMilliseconds:F0} ms | " +
            $"direct doc {directDocument.TotalMilliseconds:F0} ms, full {directElapsed.TotalMilliseconds:F0} ms | " +
            $"tunnel overhead {overhead:P0} ({(tunnelElapsed - directElapsed).TotalMilliseconds:F0} ms)");

        Assert.True(tunnelElapsed < TimeSpan.FromMinutes(2));
        Assert.True(directElapsed < TimeSpan.FromMinutes(2));
    }

    // ---------- 3) A sustained video stream with 20 other streams ----------

    [Fact]
    public async Task SustainedVideoStream_NoStall_NoMemoryGrowth()
    {
        var rttMs = 150;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("RB_VIDEO_SECONDS"), out var s) && s > 0 ? s : 60;
        const long bitrate = 5_000_000;
        const int background = 20;
        var duration = TimeSpan.FromSeconds(seconds);

        await using var wan = await WanPair.CreateAsync(LinkProfile.FromRtt(rttMs));
        var video = new PacedTarget(bitrate, duration);
        wan.Host.OpenRequested = (req, _) => Task.FromResult(MuxOpenDecision.Ok(
            req.Host == "video.test" ? video : new ResourceTarget()));

        var videoOpen = await wan.Guest.OpenStreamAsync("video.test", 443, CancellationToken.None);
        Assert.True(videoOpen.IsOpen);
        await using var videoStream = videoOpen.Stream!;

        using var backgroundCts = new CancellationTokenSource();
        var backgroundTasks = Enumerable.Range(0, background).Select(i => Task.Run(async () =>
        {
            var open = await wan.Guest.OpenStreamAsync($"bg{i}.test", 443, CancellationToken.None);
            if (!open.IsOpen) return 0L;
            await using var stream = open.Stream!;
            long moved = 0;
            try
            {
                while (!backgroundCts.IsCancellationRequested)
                {
                    await ResourceProtocol.RequestAsync(stream, 32 * 1024, backgroundCts.Token);
                    await ResourceProtocol.ReadExactAsync(stream, 32 * 1024, backgroundCts.Token);
                    moved += 32 * 1024;
                    await Task.Delay(200, backgroundCts.Token);
                }
            }
            catch (Exception) { /* cancelled with the end of the measurement */ }
            return moved;
        })).ToArray();

        var baseline = Perf.SettledMemory();
        var gaps = new List<double>();
        var buffer = new byte[64 * 1024];
        long received = 0;
        long midMemory = 0;
        var clock = Stopwatch.StartNew();
        var lastArrival = clock.Elapsed.TotalMilliseconds;
        while (true)
        {
            var n = await videoStream.ReadAsync(buffer).AsTask().WaitAsync(Limit);
            if (n == 0) break;
            received += n;
            var now = clock.Elapsed.TotalMilliseconds;
            gaps.Add(now - lastArrival);
            lastArrival = now;
            if (midMemory == 0 && received > video.TotalBytes / 2) midMemory = GC.GetTotalMemory(forceFullCollection: false);
        }
        var elapsed = clock.Elapsed;
        backgroundCts.Cancel();
        var backgroundBytes = (await Task.WhenAll(backgroundTasks)).Sum();
        var settled = Perf.SettledMemory();

        gaps.Sort();
        var maxGap = gaps.Count == 0 ? 0 : gaps[^1];
        _output.WriteLine(
            $"[video] RTT {rttMs} ms | {bitrate / 1_000_000.0:F0} Mbit/s for {seconds} s + {background} background streams | " +
            $"received {received / Perf.MB:F1} MB in {elapsed.TotalSeconds:F1} s = {Perf.Rate(received, elapsed)} | " +
            $"background {backgroundBytes / Perf.MB:F1} MB | " +
            $"inter-arrival p50 {Perf.Percentile(gaps, 0.50):F1} ms, p95 {Perf.Percentile(gaps, 0.95):F1} ms, p99 {Perf.Percentile(gaps, 0.99):F1} ms, max {maxGap:F0} ms | " +
            $"managed memory {baseline / 1024.0 / 1024:F1} → {midMemory / 1024.0 / 1024:F1} (mid) → {settled / 1024.0 / 1024:F1} MiB");

        Assert.Equal(video.TotalBytes, received);
        // No stall: the link carries 5 Mbit/s with a large margin, so any gap beyond two seconds is a real stall.
        Assert.True(maxGap < 2000, $"stall: {maxGap:F0} ms without data");
        // The stream finished in its time rather than well after it (no delay accumulated).
        Assert.True(elapsed < duration + TimeSpan.FromSeconds(10), $"video took {elapsed.TotalSeconds:F1} s for a {seconds} s stream");
        // No unbounded growth: a 1 MiB window per stream and 21 streams => a theoretical ceiling of 21 MiB buffered.
        Assert.True(settled - baseline < 64 * MiB, $"managed memory grew by {(settled - baseline) / (double)MiB:F1} MiB");
        Assert.True(wan.Host.Stats.OpenStreams <= background + 1, $"stream leak: {wan.Host.Stats.OpenStreams} open on the host");
    }

    // ---------- 4) 256 concurrent streams at an RTT of 150 ms with slow consumers ----------

    [Fact]
    public async Task Concurrent256Streams_SlowConsumersDoNotStarveTheRest()
    {
        const int rttMs = 150;
        const int total = 256;      // the host's limit in docs/protocol.md section 5
        const int stalled = 32;     // of which: a stalled consumer at the destination
        const long size = 256 * 1024;
        const long pushIntoStalled = 8 * MiB;   // far larger than the window: with no window it would all have gone through

        await using var wan = await WanPair.CreateAsync(LinkProfile.FromRtt(rttMs));
        var measuredRtt = await wan.MeasureRttAsync(3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledTargets = new ConcurrentBag<TestTarget>();
        var healthy = new ConcurrentBag<ResourceTarget>();
        wan.Host.OpenRequested = (req, _) =>
        {
            if (req.Port == 1)
            {
                var slow = new TestTarget(gate: gate);
                stalledTargets.Add(slow);
                return Task.FromResult(MuxOpenDecision.Ok(slow));
            }
            var target = new ResourceTarget();
            healthy.Add(target);
            return Task.FromResult(MuxOpenDecision.Ok(target));
        };

        var openClock = Perf.Start();
        var opens = await Task.WhenAll(Enumerable.Range(0, total).Select(i =>
            wan.Guest.OpenStreamAsync($"s{i}.test", i < stalled ? 1 : 443, CancellationToken.None))).WaitAsync(Limit);
        var openElapsed = openClock.Elapsed;
        Assert.All(opens, o => Assert.True(o.IsOpen, o.Reason?.ToString()));
        Assert.Equal(total, wan.Guest.Stats.OpenStreams);

        // Nothing moves before this moment: the healthy destinations only answer a request.
        var clock = Perf.Start();
        var stalledWritten = new long[stalled];
        var stalledWriters = Enumerable.Range(0, stalled).Select(i => Task.Run(async () =>
        {
            var stream = opens[i].Stream!;
            var chunk = new byte[64 * 1024];
            for (long written = 0; written < pushIntoStalled; written += chunk.Length)
            {
                await stream.WriteAsync(chunk);
                Interlocked.Add(ref stalledWritten[i], chunk.Length);
            }
        })).ToArray();

        await Task.WhenAll(Enumerable.Range(stalled, total - stalled).Select(async i =>
        {
            var stream = opens[i].Stream!;
            await ResourceProtocol.RequestAsync(stream, size, CancellationToken.None);
            await ResourceProtocol.ReadExactAsync(stream, size, CancellationToken.None);
        })).WaitAsync(Limit);
        var healthyElapsed = clock.Elapsed;

        // A snapshot while the consumers are stalled: how much the stalled streams accepted, and how much their destinations received.
        var perStalled = stalledWritten.Sum() / (double)stalled;
        var receivedByStalledSinks = stalledTargets.Sum(t => t.Received);
        Assert.False(stalledWriters.Any(t => t.IsCompleted), "the stalled streams drained without their consumer moving");

        gate.SetResult();
        await Task.WhenAll(stalledWriters).WaitAsync(Limit);
        var drainedElapsed = clock.Elapsed;
        foreach (var open in opens)
        {
            await ((IHalfClosable)open.Stream!).CompleteWritingAsync(CancellationToken.None);
            await open.Stream!.DisposeAsync();
        }

        var healthyBytes = (total - stalled) * size;
        _output.WriteLine(
            $"[256] RTT {measuredRtt.TotalMilliseconds:F0} ms | {total} streams opened in {openElapsed.TotalMilliseconds:F0} ms " +
            $"({openElapsed.TotalMilliseconds / measuredRtt.TotalMilliseconds:F1} RTT, {openElapsed.TotalMilliseconds / total:F2} ms each) | " +
            $"{stalled} stalled consumers accepted {perStalled / MiB:F2} MiB each of {pushIntoStalled / MiB} MiB offered (sinks received {receivedByStalledSinks}) | " +
            $"the other {total - stalled} fetched {healthyBytes / Perf.MB:F0} MB in {healthyElapsed.TotalMilliseconds:F0} ms " +
            $"({healthyElapsed.TotalMilliseconds / measuredRtt.TotalMilliseconds:F1} RTT) = {Perf.Rate(healthyBytes, healthyElapsed)} | " +
            $"stalled drained after release at {drainedElapsed.TotalMilliseconds:F0} ms");

        // Window isolation: the stalled consumer received nothing, and the sender stopped at about one window rather than at 8 MiB.
        Assert.Equal(0, receivedByStalledSinks);
        Assert.True(perStalled <= 4 * MiB, $"no isolation: each stalled stream accepted {perStalled / MiB:F1} MiB");
        // And the rest finished their work in full during it, in a time governed by the RTT rather than by the stall.
        Assert.Equal(total - stalled, healthy.Count);
        Assert.True(healthyElapsed < TimeSpan.FromSeconds(10), $"healthy streams took {healthyElapsed.TotalSeconds:F1} s");
    }
}
