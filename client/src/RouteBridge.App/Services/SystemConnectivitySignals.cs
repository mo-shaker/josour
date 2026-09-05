using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RouteBridge.Infrastructure.Control;

namespace RouteBridge.App.Services;

/// <summary>
/// The Windows source of <see cref="ConnectivitySignal"/>s (plan 8.5: نوم الجهاز / تغيير الشبكة):
/// <see cref="SystemEvents.PowerModeChanged"/> with <see cref="PowerModes.Resume"/>, and
/// <see cref="NetworkChange.NetworkAddressChanged"/>.
/// <para>
/// It lives in the app rather than in Infrastructure because <see cref="SystemEvents"/> is part of the Windows desktop
/// framework; <c>ConnectivityWatcher</c> — everything that decides what to DO with a signal — is in Infrastructure behind
/// <see cref="IConnectivitySignals"/> and is tested there with a hand-driven source.
/// </para>
/// <para>
/// Both events are raised on operating-system threads, so subscribers must be quick and must not throw; the watcher
/// guarantees both. Subscribing to <see cref="SystemEvents"/> keeps a strong reference to this instance for the life of
/// the process, which is why <see cref="Dispose"/> matters even at shutdown.
/// </para>
/// </summary>
public sealed class SystemConnectivitySignals : IConnectivitySignals, IDisposable
{
    private readonly ILogger<SystemConnectivitySignals> _logger;
    private readonly object _gate = new();
    private bool _subscribed;
    private bool _disposed;

    public SystemConnectivitySignals(ILogger<SystemConnectivitySignals> logger) => _logger = logger;

    public event Action<ConnectivitySignal>? Signalled;

    /// <summary>Starts listening. A no-op off Windows (the app only ships there; this keeps the composition root simple).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_subscribed || _disposed)
            {
                return;
            }

            _subscribed = true;
        }

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }

            _logger.LogInformation("Listening for power-resume and network-address-change events");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            // Losing these events costs a faster reconnect, not correctness: the backoff still gets there.
            _logger.LogWarning(ex, "Power and network change events are unavailable; reconnects will wait out the backoff");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Raise(ConnectivitySignal.PowerResumed);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Raise(ConnectivitySignal.NetworkAddressChanged);

    private void Raise(ConnectivitySignal signal)
    {
        try
        {
            Signalled?.Invoke(signal);
        }
        catch (Exception ex)
        {
            // We are on an OS callback thread: an escaping exception would take the process with it.
            _logger.LogError(ex, "A connectivity-signal handler threw for {Signal}", signal);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_subscribed)
            {
                return;
            }
        }

        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug("Unsubscribing from the power/network events failed: {Message}", ex.Message);
        }
    }
}
