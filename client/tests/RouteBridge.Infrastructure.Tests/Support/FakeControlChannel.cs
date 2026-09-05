using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Control;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// A control channel that only holds a state — enough for the pieces that react to one (<see cref="ConnectivityWatcher"/>)
/// without a socket. <see cref="WakelessControlChannel"/> is the same thing without <see cref="IReconnectNow"/>, for the
/// path where the channel cannot be woken.
/// </summary>
public class WakelessControlChannel : IControlChannel
{
    public ControlChannelState State { get; set; } = ControlChannelState.Connected;

    public event Action<ControlChannelState>? StateChanged;

    public event Action<ControlMessage>? MessageReceived;

    public event Action<ControlChannelClosed>? Closed;

    /// <summary>Moves the channel to a state and tells the subscribers, like the real one does.</summary>
    public void MoveTo(ControlChannelState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Publish(ControlMessage message) => MessageReceived?.Invoke(message);

    public void PublishClosed(ControlChannelClosed closed) => Closed?.Invoke(closed);

    public Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task SendAsync(ControlMessage message, CancellationToken ct) => Task.CompletedTask;

    public Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary><see cref="WakelessControlChannel"/> that records every <see cref="IReconnectNow.ReconnectNow"/> call.</summary>
public sealed class FakeControlChannel : WakelessControlChannel, IReconnectNow
{
    private readonly List<string> _wakes = new();

    public IReadOnlyList<string> Wakes
    {
        get
        {
            lock (_wakes)
            {
                return _wakes.ToList();
            }
        }
    }

    public void ReconnectNow(string reason)
    {
        lock (_wakes)
        {
            _wakes.Add(reason);
        }
    }
}
