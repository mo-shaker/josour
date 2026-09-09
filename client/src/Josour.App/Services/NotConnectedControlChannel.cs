using Josour.Core.Control;

namespace Josour.App.Services;

/// <summary>
/// A channel that is never connected: every call fails with a clear message. Since week 3 the app resolves
/// <c>Josour.Infrastructure.ControlChannel</c> (or <c>MockControlChannel</c> with <c>--mock</c>), which behaves exactly like
/// this one until <see cref="ControlChannelConnector"/> has a server address and a signed-in session; this class stays as the
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

    public event Action<ControlChannelClosed>? Closed
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
