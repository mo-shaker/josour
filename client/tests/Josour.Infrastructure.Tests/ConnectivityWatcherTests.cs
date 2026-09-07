using Josour.Core.Control;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// Plan 8.5 (نوم الجهاز / تغيير الشبكة): waking up or changing network makes the control channel look at its connection
/// at once instead of waiting out a backoff that was budgeted for a different problem.
/// </summary>
public sealed class ConnectivityWatcherTests
{
    private static ConnectivityWatcher Create(
        WakelessControlChannel channel,
        FakeConnectivitySignals signals,
        TestClock? clock = null,
        Func<CancellationToken, Task>? reconnect = null,
        TimeSpan? quiet = null) =>
        new(
            channel,
            signals,
            logger: null,
            clock ?? new TestClock(),
            new ConnectivityWatcherOptions
            {
                Quiet = quiet ?? TimeSpan.FromSeconds(2),
                Reconnect = reconnect,
            });

    [Fact]
    public void Resume_WakesTheChannel()
    {
        var channel = new FakeControlChannel { State = ControlChannelState.Reconnecting };
        var signals = new FakeConnectivitySignals();
        using var watcher = Create(channel, signals);

        signals.Resume();

        Assert.Equal(1, watcher.Handled);
        Assert.Contains("resumed", Assert.Single(channel.Wakes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkAddressChange_WakesTheChannel()
    {
        var channel = new FakeControlChannel { State = ControlChannelState.Connected };
        var signals = new FakeConnectivitySignals();
        using var watcher = Create(channel, signals);

        signals.NetworkChanged();

        Assert.Equal(1, watcher.Handled);
        Assert.Contains("network", Assert.Single(channel.Wakes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABurstOfSignals_IsOneWake()
    {
        // A resume is followed by several address changes as the interfaces come back: one reconnect, not five.
        var channel = new FakeControlChannel();
        var signals = new FakeConnectivitySignals();
        var clock = new TestClock();
        using var watcher = Create(channel, signals, clock);

        signals.Resume();
        signals.NetworkChanged();
        signals.NetworkChanged();

        Assert.Equal(1, watcher.Handled);
        Assert.Single(channel.Wakes);

        // …and once the burst is over, the next event counts again.
        clock.Advance(TimeSpan.FromSeconds(3));
        signals.NetworkChanged();

        Assert.Equal(2, watcher.Handled);
        Assert.Equal(2, channel.Wakes.Count);
    }

    [Fact]
    public async Task WhenTheChannelIsNotRunningAtAll_TheReconnectCallbackOpensIt()
    {
        var channel = new FakeControlChannel { State = ControlChannelState.Disconnected };
        var signals = new FakeConnectivitySignals();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = Create(channel, signals, reconnect: _ =>
        {
            opened.TrySetResult();
            return Task.CompletedTask;
        });

        signals.Resume();

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(channel.Wakes);
    }

    [Fact]
    public async Task WhileTheChannelIsConnected_NothingIsReopened()
    {
        var channel = new FakeControlChannel { State = ControlChannelState.Connected };
        var signals = new FakeConnectivitySignals();
        var reconnects = 0;
        using var watcher = Create(channel, signals, reconnect: _ =>
        {
            Interlocked.Increment(ref reconnects);
            return Task.CompletedTask;
        });

        signals.Resume();
        await Task.Delay(50);

        Assert.Equal(0, Volatile.Read(ref reconnects)); // the wake is enough; the supervisor owns the socket
        Assert.Single(channel.Wakes);
    }

    [Fact]
    public async Task AReconnectCallbackThatThrows_DoesNotEscapeTheOsCallback()
    {
        var channel = new FakeControlChannel { State = ControlChannelState.Disconnected };
        var signals = new FakeConnectivitySignals();
        using var watcher = Create(channel, signals, reconnect: _ => Task.FromException(new InvalidOperationException("no server")));

        signals.Resume(); // must not throw: this runs on a Windows event thread
        await Task.Delay(50);

        Assert.Equal(1, watcher.Handled);
    }

    [Fact]
    public async Task AChannelThatCannotBeWoken_StillGetsTheReconnectCallback()
    {
        var channel = new WakelessControlChannel { State = ControlChannelState.Disconnected };
        var signals = new FakeConnectivitySignals();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = Create(channel, signals, reconnect: _ =>
        {
            opened.TrySetResult();
            return Task.CompletedTask;
        });

        signals.Resume();

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var channel = new FakeControlChannel();
        var signals = new FakeConnectivitySignals();
        var watcher = Create(channel, signals);

        watcher.Dispose();
        signals.Resume();

        Assert.False(signals.HasSubscribers);
        Assert.Equal(0, watcher.Handled);
        Assert.Empty(channel.Wakes);
    }
}
