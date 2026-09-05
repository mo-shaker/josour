namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> the test drives by hand, timers included: <see cref="Advance"/> moves the clock to every due
/// time in order and fires the timers exactly as a real clock would, so a 30 s stats cadence or a 10 s probe deadline can be
/// tested without waiting. <see cref="GetTimestamp"/> is the same clock, which is what the monotonic countdown uses.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Live (not yet disposed) timers; a test can wait for one to be armed before advancing.</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that becomes due on the way (in due order).</summary>
    public void Advance(TimeSpan delta)
    {
        var target = GetUtcNow() + delta;
        while (true)
        {
            FakeTimer? next = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is { } due && (next?.DueAt is not { } best || due < best))
                    {
                        next = timer;
                    }
                }
            }

            if (next?.DueAt is not { } dueAt || dueAt > target)
            {
                break;
            }

            lock (_gate)
            {
                _now = dueAt;
            }

            next.Fire();
        }

        lock (_gate)
        {
            _now = target;
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly TestClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public FakeTimer(TestClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        /// <summary>When the timer fires next, or null when it is disarmed.</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _clock.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : _clock.GetUtcNow() + _period;
            _callback(_state);
        }

        public void Dispose()
        {
            DueAt = null;
            _clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
