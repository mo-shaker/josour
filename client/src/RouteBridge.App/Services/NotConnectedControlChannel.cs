using RouteBridge.Core.Control;

namespace RouteBridge.App.Services;

/// <summary>
/// A channel that is never connected: every call fails with a clear message. Not registered in DI since week 2
/// (<c>MockControlChannel</c> took its place; week 3 brings <c>RouteBridge.Infrastructure.ControlChannel</c>); kept as the
/// explicit "not signed in / no server" stand-in for design-time data and tests.
/// </summary>
public sealed class NotConnectedControlChannel : IControlChannel
{
    private const string Reason = "The control channel is not connected.";

    public ControlChannelState State => ControlChannelState.Disconnected;

    public event Action<ControlChannelState>? StateChanged
    {
        add { }
        remove { }
    }

    public event Action<ControlMessage>? MessageReceived
    {
        add { }
        remove { }
    }

    public Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct) =>
        Task.FromException<HelloAckMessage>(new InvalidOperationException(Reason));

    public Task SendAsync(ControlMessage message, CancellationToken ct) =>
        Task.FromException(new InvalidOperationException(Reason));

    public Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct) =>
        Task.FromException<ControlMessage>(new InvalidOperationException(Reason));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
