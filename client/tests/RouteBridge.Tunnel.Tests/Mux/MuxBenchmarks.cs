using System.Diagnostics;
using RouteBridge.Tunnel.Mux;
using Xunit.Abstractions;

namespace RouteBridge.Tunnel.Tests.Mux;

/// <summary>معايير ADR-0006 فوق TLS محلي. الأرقام تُطبع في مخرجات الاختبار وتُسجَّل في الـ ADR.</summary>
[Trait("Category", "Benchmark")]
public class MuxBenchmarks
{
    private const long MiB = 1024 * 1024;
    private const long MB = 1_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private readonly ITestOutputHelper _output;

    public MuxBenchmarks(ITestOutputHelper output) => _output = output;

    // (a) مستهلك بطيء على القناة A لا يعطل القناة B: A متوقفة 3 ثوانٍ، نقيس ما تنقله B خلالها.
    [Fact]
    public async Task SlowConsumerOnA_DoesNotStallB()
    {
        await using var pair = await MuxPair.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetA = new TestTarget(gate: gate);
        var targetB = new TestTarget();
        pair.Host.OpenRequested = (req, _) => Task.FromResult(MuxOpenDecision.Ok(req.Port == 1 ? targetA : targetB));

        var a = (await pair.Guest.OpenStreamAsync("a.test", 1, CancellationToken.None)).Stream!;
        var b = (await pair.Guest.OpenStreamAsync("b.test", 2, CancellationToken.None)).Stream!;

        long writtenToA = 0;
        var aWriter = Task.Run(async () =>
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 64 * 16; i++) // 64 MiB إن لم يكن هناك ضغط عكسي
            {
                await a.WriteAsync(chunk);
                Interlocked.Add(ref writtenToA, chunk.Length);
            }
            await ((IHalfClosable)a).CompleteWritingAsync(CancellationToken.None);
        });

        var window = TimeSpan.FromSeconds(3);
        var clock = Stopwatch.StartNew();
        long writtenToB = 0;
        var chunkB = new byte[64 * 1024];
        while (clock.Elapsed < window)
        {
            await b.WriteAsync(chunkB);
            writtenToB += chunkB.Length;
        }
        var bElapsed = clock.Elapsed;
        var aStalledAt = Volatile.Read(ref writtenToA);
        var aReceivedWhileGated = targetA.Received;
        gate.SetResult();
        await aWriter.WaitAsync(Timeout);
        await ((IHalfClosable)b).CompleteWritingAsync(CancellationToken.None);
        await StreamIo.ReadToEndAsync(a).WaitAsync(Timeout);
        await StreamIo.ReadToEndAsync(b).WaitAsync(Timeout);

        var bMbps = writtenToB / MB / bElapsed.TotalSeconds;
        _output.WriteLine($"(a) B moved {writtenToB / MB} MB in {bElapsed.TotalMilliseconds:F0} ms = {bMbps:F0} MB/s while A was paused; A accepted {aStalledAt / MiB:F1} MiB before stalling (sink got {aReceivedWhileGated})");
        Assert.Equal(0, aReceivedWhileGated);
        Assert.True(writtenToB >= 30 * MB, $"B moved only {writtenToB} bytes in 3 s while A was stalled");
        Assert.True(aStalledAt <= 8 * MiB, $"no backpressure: A accepted {aStalledAt} bytes while its consumer was paused");
        Assert.Equal(64 * MiB, targetA.Received);
        Assert.Equal(writtenToB, targetB.Received);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    // (c) 100 MB على قناة واحدة في الاتجاهين.
    [Fact]
    public async Task Throughput_100MB_SingleChannel_BothDirections()
    {
        await using var pair = await MuxPair.CreateAsync();
        const long size = 100 * MB;

        var up = new TestTarget();
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(up));
        var upStream = (await pair.Guest.OpenStreamAsync("up.test", 443, CancellationToken.None)).Stream!;
        var clock = Stopwatch.StartNew();
        await StreamIo.WriteAllAsync(upStream, size);
        await ((IHalfClosable)upStream).CompleteWritingAsync(CancellationToken.None);
        await StreamIo.ReadToEndAsync(upStream).WaitAsync(Timeout); // EOF يعود بعد أن استهلك المضيف كل شيء
        var upElapsed = clock.Elapsed;
        Assert.Equal(size, up.Received);
        await upStream.DisposeAsync();

        var down = new TestTarget(produce: size);
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(down));
        var downStream = (await pair.Guest.OpenStreamAsync("down.test", 443, CancellationToken.None)).Stream!;
        clock.Restart();
        var read = await StreamIo.ReadToEndAsync(downStream).WaitAsync(Timeout);
        var downElapsed = clock.Elapsed;
        Assert.Equal(size, read);
        await downStream.DisposeAsync();

        _output.WriteLine($"(c) 100 MB guest→host: {upElapsed.TotalMilliseconds:F0} ms = {size / MB / upElapsed.TotalSeconds:F0} MB/s; host→guest: {downElapsed.TotalMilliseconds:F0} ms = {size / MB / downElapsed.TotalSeconds:F0} MB/s; transport up={pair.Guest.Stats.BytesUp} down={pair.Guest.Stats.BytesDown}");
        Assert.True(upElapsed < TimeSpan.FromSeconds(60));
        Assert.True(downElapsed < TimeSpan.FromSeconds(60));
    }

    // (d) 256 قناة متزامنة تنقل كل منها 1 MB صعودًا و1 MB هبوطًا.
    [Fact]
    public async Task Concurrent_256Channels_1MB_Each()
    {
        await using var pair = await MuxPair.CreateAsync();
        const int channels = 256;
        const long size = 1 * MB;
        var targets = new System.Collections.Concurrent.ConcurrentBag<TestTarget>();
        pair.Host.OpenRequested = (_, _) =>
        {
            var t = new TestTarget(produce: size);
            targets.Add(t);
            return Task.FromResult(MuxOpenDecision.Ok(t));
        };

        var clock = Stopwatch.StartNew();
        var opens = await Task.WhenAll(Enumerable.Range(0, channels).Select(i => pair.Guest.OpenStreamAsync($"c{i}.test", 443, CancellationToken.None)));
        var openElapsed = clock.Elapsed;
        Assert.All(opens, o => Assert.True(o.IsOpen, o.Reason?.ToString()));
        Assert.Equal(channels, pair.Guest.Stats.OpenStreams);

        await Task.WhenAll(opens.Select(async o =>
        {
            var s = o.Stream!;
            var reader = StreamIo.ReadToEndAsync(s);
            await StreamIo.WriteAllAsync(s, size);
            await ((IHalfClosable)s).CompleteWritingAsync(CancellationToken.None);
            Assert.Equal(size, await reader);
            await s.DisposeAsync();
        })).WaitAsync(Timeout);
        var total = clock.Elapsed;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((pair.Host.Stats.OpenStreams > 0 || pair.Guest.Stats.OpenStreams > 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        _output.WriteLine($"(d) {channels} channels opened in {openElapsed.TotalMilliseconds:F0} ms; {channels} × 1 MB each way done in {total.TotalMilliseconds:F0} ms = {2 * channels * size / MB / total.TotalSeconds:F0} MB/s aggregate");
        Assert.Equal(channels, targets.Count);
        Assert.All(targets, t => Assert.Equal(size, t.Received));
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        Assert.True(total < TimeSpan.FromSeconds(60));
    }

    // 1000 فتح متتالٍ (خطة القسم 11): لا تسريب في عدّاد الـ streams.
    [Fact]
    public async Task Sequential_1000Opens_NoLeak()
    {
        await using var pair = await MuxPair.CreateAsync();
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new TestTarget(produce: 16)));
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var o = await pair.Guest.OpenStreamAsync("seq.test", 443, CancellationToken.None);
            Assert.True(o.IsOpen);
            await using var s = o.Stream!;
            await s.WriteAsync(new byte[16]);
            await ((IHalfClosable)s).CompleteWritingAsync(CancellationToken.None);
            Assert.Equal(16, await StreamIo.ReadToEndAsync(s));
        }
        var elapsed = clock.Elapsed;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((pair.Host.Stats.OpenStreams > 0 || pair.Guest.Stats.OpenStreams > 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        _output.WriteLine($"1000 sequential open/close in {elapsed.TotalMilliseconds:F0} ms = {elapsed.TotalMilliseconds / 1000:F2} ms each");
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
    }
}
