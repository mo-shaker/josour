using System.Buffers.Binary;
using System.Diagnostics;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>
/// The measuring instrument is measured first: these tests prove that <see cref="LatencyStream"/> adds the delay it claims,
/// respects the bandwidth ceiling, does not reorder, and does not corrupt the bytes when modelling loss. Without the Benchmark tag: they are fast.
/// </summary>
public class LinkSimulatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task<(Stream Sender, Stream Receiver)> LinkAsync(LinkProfile profile)
    {
        var (a, b) = await Loopback.CreatePairAsync();
        var link = new SimulatedLink(profile);
        return (link.WrapA(a), b);
    }

    [Fact]
    public async Task OneWayLatency_DelaysDelivery()
    {
        var (sender, receiver) = await LinkAsync(new LinkProfile { OneWayLatency = TimeSpan.FromMilliseconds(60) });
        await using (sender)
        await using (receiver)
        {
            var clock = Stopwatch.StartNew();
            await sender.WriteAsync(new byte[] { 7 });
            var buffer = new byte[1];
            await receiver.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
            var elapsed = clock.Elapsed;
            Assert.Equal(7, buffer[0]);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(50), $"arrived after only {elapsed.TotalMilliseconds:F0} ms");
            Assert.True(elapsed < TimeSpan.FromMilliseconds(400), $"arrived after {elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [Fact]
    public async Task Bandwidth_IsCapped()
    {
        // 8 Mbit/s => 1 MB/s => 256 KiB needs about 262 ms on the wire.
        var (sender, receiver) = await LinkAsync(new LinkProfile { BitsPerSecond = 8_000_000 });
        await using (sender)
        await using (receiver)
        {
            const int size = 256 * 1024;
            var clock = Stopwatch.StartNew();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[size];
                await receiver.ReadExactlyAsync(buffer);
                return clock.Elapsed;
            });
            await sender.WriteAsync(new byte[size]);
            var elapsed = await reader.WaitAsync(Timeout);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(200), $"{size} B arrived in {elapsed.TotalMilliseconds:F0} ms; the cap was not applied");
            Assert.True(elapsed < TimeSpan.FromMilliseconds(900), $"{size} B took {elapsed.TotalMilliseconds:F0} ms; far slower than the cap");
        }
    }

    [Fact]
    public async Task Jitter_DoesNotReorderBytes()
    {
        var profile = new LinkProfile
        {
            OneWayLatency = TimeSpan.FromMilliseconds(20),
            Jitter = TimeSpan.FromMilliseconds(18),
        };
        var (sender, receiver) = await LinkAsync(profile);
        await using (sender)
        await using (receiver)
        {
            const int chunks = 60;
            var send = Task.Run(async () =>
            {
                for (var i = 0; i < chunks; i++)
                {
                    var payload = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(payload, i);
                    await sender.WriteAsync(payload);
                }
            });
            var buffer = new byte[chunks * 4];
            await receiver.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
            await send;
            for (var i = 0; i < chunks; i++)
                Assert.Equal(i, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(i * 4, 4)));
        }
    }

    [Fact]
    public async Task Loss_AddsDelay_ButNeverCorruptsOrDrops()
    {
        // Certain loss for every packet: the stream arrives complete and intact, but late by the retransmission penalty.
        var profile = new LinkProfile
        {
            LossRate = 1.0,
            LossPenalty = TimeSpan.FromMilliseconds(30),
            PacketBytes = 1460,
        };
        var (sender, receiver) = await LinkAsync(profile);
        await using (sender)
        await using (receiver)
        {
            var payload = new byte[4 * 1460];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
            var clock = Stopwatch.StartNew();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[payload.Length];
                await receiver.ReadExactlyAsync(buffer);
                return buffer;
            });
            await sender.WriteAsync(payload);
            var received = await reader.WaitAsync(Timeout);
            Assert.Equal(payload, received);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(100), $"loss penalty was not applied ({clock.Elapsed.TotalMilliseconds:F0} ms for 4 lost packets × 30 ms)");
        }
    }

    [Fact]
    public async Task SharedMedium_SharesTheBottleneck()
    {
        // Two connections sharing a wire at 8 Mbit/s: 2 x 128 KiB take about the time of 256 KiB rather than half of it.
        var medium = new LinkMedium(new LinkProfile { BitsPerSecond = 8_000_000 });
        var (a1, b1) = await Loopback.CreatePairAsync();
        var (a2, b2) = await Loopback.CreatePairAsync();
        await using var s1 = new LatencyStream(a1, medium);
        await using var s2 = new LatencyStream(a2, medium);
        await using (b1)
        await using (b2)
        {
            const int size = 128 * 1024;
            var clock = Stopwatch.StartNew();
            var readers = Task.WhenAll(
                Task.Run(async () => { var buf = new byte[size]; await b1.ReadExactlyAsync(buf); }),
                Task.Run(async () => { var buf = new byte[size]; await b2.ReadExactlyAsync(buf); }));
            await s1.WriteAsync(new byte[size]);
            await s2.WriteAsync(new byte[size]);
            await readers.WaitAsync(Timeout);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"both transfers finished in {clock.Elapsed.TotalMilliseconds:F0} ms; the shared cap was not shared");
        }
    }

    [Fact]
    public async Task MeasuredRttThroughTheMux_MatchesTheProfile()
    {
        // End-to-end verification: a PING/PONG over TLS+the mux on the link gives the requested RTT.
        await using var wan = await WanPair.CreateAsync(LinkProfile.FromRtt(80));
        var rtt = await wan.MeasureRttAsync(3);
        Assert.True(rtt >= TimeSpan.FromMilliseconds(70), $"measured RTT {rtt.TotalMilliseconds:F1} ms is below the 80 ms profile");
        Assert.True(rtt <= TimeSpan.FromMilliseconds(200), $"measured RTT {rtt.TotalMilliseconds:F1} ms is far above the 80 ms profile");
    }
}
