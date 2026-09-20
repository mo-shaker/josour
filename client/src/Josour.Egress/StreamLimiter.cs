namespace Josour.Egress;

/// <summary>
/// The host's limits (docs/protocol.md section 5): concurrent streams per the window's band, and 50 OPENs a second (a sliding window).
/// Exceeding them = OPEN_FAIL(limit).
///
/// <b>The concurrency limit follows the window</b> after the week-5 amendment: 256 at 1 MiB, 128 at 2 MiB, 64 at 4 MiB — that is,
/// the window x the limit = 256 MiB in every band. What passes it is <c>EgressTunnelAdapter.Create</c> from
/// <c>TunnelEgressContext.MaxConcurrentStreams</c>, which <c>TunnelSession</c> derives from the RTT.
/// The opens-per-second limit has nothing to do with the window, so it did not change.
/// </summary>
public sealed class StreamLimiter
{
    /// <summary>The limit at the lowest window (1 MiB); the contract's value before the amendment and its cap after it.</summary>
    public const int DefaultMaxConcurrent = 256;
    public const int DefaultMaxOpensPerSecond = 50;

    private readonly int _maxConcurrent;
    private readonly int _maxOpensPerSecond;
    private readonly Func<long> _nowTicks;
    private readonly Queue<long> _recentOpens = new();
    private readonly object _gate = new();
    private int _active;

    public StreamLimiter(int maxConcurrent = DefaultMaxConcurrent, int maxOpensPerSecond = DefaultMaxOpensPerSecond, Func<long>? nowTicks = null)
    {
        if (maxConcurrent < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        if (maxOpensPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(maxOpensPerSecond));
        _maxConcurrent = maxConcurrent;
        _maxOpensPerSecond = maxOpensPerSecond;
        _nowTicks = nowTicks ?? (() => Environment.TickCount64);
    }

    public int Active => Volatile.Read(ref _active);
    public int MaxConcurrent => _maxConcurrent;
    public int MaxOpensPerSecond => _maxOpensPerSecond;

    /// <summary>Reserves a slot for a new stream; null when either limit is exceeded. Disposing the reservation frees the slot.</summary>
    public Lease? TryAcquire()
    {
        lock (_gate)
        {
            var now = _nowTicks();
            while (_recentOpens.Count > 0 && now - _recentOpens.Peek() >= 1000) _recentOpens.Dequeue();
            if (_active >= _maxConcurrent) return null;
            if (_recentOpens.Count >= _maxOpensPerSecond) return null;
            _recentOpens.Enqueue(now);
            _active++;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_gate) _active--;
    }

    public sealed class Lease : IDisposable
    {
        private StreamLimiter? _owner;

        internal Lease(StreamLimiter owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
