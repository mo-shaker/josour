using System.Diagnostics;
using System.Threading.Channels;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>
/// The properties of one network link in one direction. The link is full duplex: one <see cref="LinkMedium"/> per direction.
/// </summary>
/// <remarks>
/// <para><b>What is modelled and why:</b> what is below and above it is a reliable, ordered TCP byte stream, so the modelling falls on what the
/// receiver sees <i>after</i> TCP's reassembly:</para>
/// <list type="bullet">
///   <item><b>The delay</b> (<see cref="OneWayLatency"/>): half the RTT in each direction. This is the governing variable for a windowed
///     protocol: the per-stream ceiling = the window ÷ the RTT.</item>
///   <item><b>The bandwidth ceiling</b> (<see cref="BitsPerSecond"/>): a shared serialisation queue; the second write
///     does not start before the first has left the wire. Sharing the same <see cref="LinkMedium"/> between several streams gives
///     one shared bottleneck, as on a real link.</item>
///   <item><b>The jitter</b> (<see cref="Jitter"/>): variation in the arrival time, <b>with no</b> reordering — the receiver behind TCP
///     never sees reordering, but variation in when the bytes are delivered. Arrival is constrained to be monotonic for that reason.</item>
///   <item><b>The loss</b> (<see cref="LossRate"/>): bytes cannot be "dropped" from a reliable stream without corrupting it. Loss appears
///     to the layer above as <b>head-of-line blocking</b>: the lost packet delays everything after it until it is retransmitted. So it is modelled
///     as a cumulative delay (<see cref="LossPenalty"/> per lost packet) affecting all the following bytes.</item>
/// </list>
/// <para><b>What is not modelled:</b> TCP's congestion window and its slow start. That is, the numbers are <i>optimistic</i> by the first few RTTs
/// of every TCP connection; that favours a conservative measurement, because the tunnel is one long-lived connection
/// (paying one slow start) while the direct path opens a connection per resource (paying it per resource).</para>
/// </remarks>
internal sealed record LinkProfile
{
    /// <summary>The one-way delay = the RTT ÷ 2.</summary>
    public TimeSpan OneWayLatency { get; init; } = TimeSpan.Zero;

    /// <summary>A uniform ± variation around the delay. It does not reorder (see the type's notes).</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.Zero;

    /// <summary>The bandwidth ceiling in bits/second; 0 = no ceiling.</summary>
    public long BitsPerSecond { get; init; }

    /// <summary>The packet loss probability (per <see cref="PacketBytes"/> bytes); 0 = no loss.</summary>
    public double LossRate { get; init; }

    /// <summary>The retransmission penalty per lost packet (an RTO). The default is 200 ms, TCP's minimum RTO.</summary>
    public TimeSpan LossPenalty { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The "packet" size used to compute the loss (a typical MSS).</summary>
    public int PacketBytes { get; init; } = 1460;

    /// <summary>The largest the wire's queue may fill before the writer is slowed (modelling a bounded buffer rather than unbounded growth).</summary>
    public TimeSpan MaxQueue { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan Rtt => OneWayLatency * 2;

    /// <summary>A profile with an RTT in milliseconds (split between the two directions) and an optional bandwidth ceiling in megabits/second.</summary>
    public static LinkProfile FromRtt(double rttMs, double megabitsPerSecond = 0) => new()
    {
        OneWayLatency = TimeSpan.FromMilliseconds(rttMs / 2.0),
        BitsPerSecond = (long)(megabitsPerSecond * 1_000_000),
        LossPenalty = TimeSpan.FromMilliseconds(Math.Max(200, rttMs)),
    };

    public override string ToString()
    {
        var bw = BitsPerSecond > 0 ? $", {BitsPerSecond / 1_000_000.0:0.##} Mbit/s" : ", unmetered";
        var jitter = Jitter > TimeSpan.Zero ? $", ±{Jitter.TotalMilliseconds:0.#} ms jitter" : string.Empty;
        var loss = LossRate > 0 ? $", {LossRate:P2} loss" : string.Empty;
        return $"RTT {Rtt.TotalMilliseconds:0.#} ms{bw}{jitter}{loss}";
    }
}

/// <summary>What is computed for one write: when it arrives, and how full the wire's queue is at that moment.</summary>
internal readonly record struct LinkSchedule(double ArrivalMs, double QueueMs);

/// <summary>
/// The wire itself in one direction: a shared clock, a shared serialisation queue, and a cumulative delay from the loss.
/// Several <see cref="LatencyStream"/>s sharing one instance => they share the same bottleneck.
/// </summary>
internal sealed class LinkMedium
{
    private readonly object _gate = new();
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _freeAtMs;
    private double _lastArrivalMs;
    private double _lossDelayMs;
    private long _bytes;
    private long _lossEvents;

    public LinkMedium(LinkProfile profile, int seed = 20260905)
    {
        Profile = profile;
        _random = new Random(seed);
    }

    public LinkProfile Profile { get; }
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public long BytesScheduled { get { lock (_gate) return _bytes; } }
    public long LossEvents { get { lock (_gate) return _lossEvents; } }

    /// <summary>It reserves the wire for this write and returns its absolute arrival time on the wire's clock.</summary>
    public LinkSchedule Schedule(int bytes, double nowMs)
    {
        lock (_gate)
        {
            var start = Math.Max(nowMs, _freeAtMs);
            var serializationMs = Profile.BitsPerSecond > 0 ? bytes * 8_000.0 / Profile.BitsPerSecond : 0;
            _freeAtMs = start + serializationMs;

            if (Profile.LossRate > 0)
            {
                var packets = Math.Max(1, (bytes + Profile.PacketBytes - 1) / Profile.PacketBytes);
                for (var i = 0; i < packets; i++)
                {
                    if (_random.NextDouble() >= Profile.LossRate) continue;
                    _lossEvents++;
                    _lossDelayMs += Profile.LossPenalty.TotalMilliseconds;
                }
            }

            var jitterMs = Profile.Jitter > TimeSpan.Zero
                ? (_random.NextDouble() * 2 - 1) * Profile.Jitter.TotalMilliseconds
                : 0;
            var arrival = _freeAtMs + Profile.OneWayLatency.TotalMilliseconds + _lossDelayMs + jitterMs;
            // A byte stream does not reorder: the arrival is monotonic whatever the jitter does.
            if (arrival < _lastArrivalMs) arrival = _lastArrivalMs;
            _lastArrivalMs = arrival;
            _bytes += bytes;
            return new LinkSchedule(arrival, _freeAtMs - nowMs);
        }
    }
}

/// <summary>
/// A <see cref="Stream"/> wrapper that turns a loopback link into a wide-area one: the write is scheduled on
/// <see cref="LinkMedium"/> and delivered to the inner stream at its time; the read passes through as it is (the delay was already applied
/// at the writing end). The composition: a raw socket -> LatencyStream -> TLS -> the mux.
/// </summary>
internal sealed class LatencyStream : Stream
{
    private readonly Stream _inner;
    private readonly LinkMedium _medium;
    private readonly Channel<Segment> _queue = Channel.CreateUnbounded<Segment>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly bool _ownsInner;
    private int _disposed;

    public LatencyStream(Stream inner, LinkMedium medium, bool ownsInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _medium = medium ?? throw new ArgumentNullException(nameof(medium));
        _ownsInner = ownsInner;
        _pump = Task.Run(PumpAsync);
    }

    private readonly record struct Segment(byte[] Data, double ArrivalMs);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return;
        var schedule = _medium.Schedule(buffer.Length, _medium.NowMs);
        var overflowMs = schedule.QueueMs - _medium.Profile.MaxQueue.TotalMilliseconds;
        // A bounded buffer: the writer is slowed rather than the queue growing without bound (as TCP's backpressure does).
        if (overflowMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(overflowMs), cancellationToken).ConfigureAwait(false);
        if (!_queue.Writer.TryWrite(new Segment(buffer.ToArray(), schedule.ArrivalMs)))
            throw new IOException("link is closed");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var segment))
                {
                    // The arrival times are absolute, so one timer error does not accumulate onto what follows.
                    var waitMs = segment.ArrivalMs - _medium.NowMs;
                    if (waitMs > 0.5) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                    await _inner.WriteAsync(segment.Data, ct).ConfigureAwait(false);
                    await _inner.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* the other side closed the socket */ }
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>It drains what is left on the wire (with a ceiling) and then closes.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        try { await _pump.WaitAsync(timeout).ConfigureAwait(false); } catch (TimeoutException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            // A short timeout to deliver what is on the wire, then the transport is dropped.
            try { _pump.Wait(TimeSpan.FromMilliseconds(250)); } catch { /* ignore */ }
            _cts.Cancel();
            if (_ownsInner) { try { _inner.Dispose(); } catch { /* ignore */ } }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            try { await _pump.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Cancel();
            if (_ownsInner) { try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ } }
            _cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>A full-duplex link: two media (one direction per side) wrapping the two ends of a socket pair.</summary>
internal sealed class SimulatedLink
{
    public SimulatedLink(LinkProfile profile, int seed = 20260905)
    {
        Profile = profile;
        AtoB = new LinkMedium(profile, seed);
        BtoA = new LinkMedium(profile, seed + 1);
    }

    public LinkProfile Profile { get; }
    /// <summary>The wire from end A to B (applied to A's writes).</summary>
    public LinkMedium AtoB { get; }
    public LinkMedium BtoA { get; }

    public Stream WrapA(Stream raw) => new LatencyStream(raw, AtoB);
    public Stream WrapB(Stream raw) => new LatencyStream(raw, BtoA);
}
