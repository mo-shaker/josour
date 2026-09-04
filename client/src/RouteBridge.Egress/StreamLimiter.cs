namespace RouteBridge.Egress;

/// <summary>
/// حدود المضيف (docs/protocol.md القسم 5): 256 stream متزامن و50 OPEN في الثانية (نافذة منزلقة). التجاوز = OPEN_FAIL(limit).
/// </summary>
public sealed class StreamLimiter
{
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

    /// <summary>يحجز مكانًا لـ stream جديد؛ null عند تجاوز أي من الحدين. التخلص من الحجز يحرر المكان.</summary>
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
