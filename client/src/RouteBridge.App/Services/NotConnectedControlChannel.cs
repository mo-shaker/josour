using RouteBridge.Core.Control;

namespace RouteBridge.App.Services;

/// <summary>
/// Week 1 stand-in so ViewModels can already take <see cref="IControlChannel"/> by constructor.
/// WEEK 2: replace the DI registration with MockControlChannel (from docs/ws-protocol.md), WEEK 3: with RouteBridge.Infrastructure.ControlChannel.
/// </summary>
public sealed class NotConnectedControlChannel : IControlChannel
{
    private const string Reason = "The control channel is not available yet (RouteBridge.Infrastructure.ControlChannel arrives in week 2/3).";

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
