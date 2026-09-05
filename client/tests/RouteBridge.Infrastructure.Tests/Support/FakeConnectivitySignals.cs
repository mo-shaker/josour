using RouteBridge.Infrastructure.Control;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// The sleep/resume and network-change source, driven by hand: what Windows raises through
/// <c>SystemEvents.PowerModeChanged</c> and <c>NetworkChange.NetworkAddressChanged</c> in production.
/// </summary>
public sealed class FakeConnectivitySignals : IConnectivitySignals
{
    public event Action<ConnectivitySignal>? Signalled;

    /// <summary>True while something is subscribed — a watcher that was disposed must not be.</summary>
    public bool HasSubscribers => Signalled is not null;

    public void Raise(ConnectivitySignal signal) => Signalled?.Invoke(signal);

    public void Resume() => Raise(ConnectivitySignal.PowerResumed);

    public void NetworkChanged() => Raise(ConnectivitySignal.NetworkAddressChanged);
}
