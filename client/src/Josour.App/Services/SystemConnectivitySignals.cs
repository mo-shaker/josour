using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Win32;
#endif
using Josour.Infrastructure.Control;

namespace Josour.App.Services;

/// <summary>
/// The platform source of <see cref="ConnectivitySignal"/>s (plan 8.5: the machine sleeping / the network changing).
/// <para>
/// A network change is <see cref="NetworkChange.NetworkAddressChanged"/> on every platform. Waking from sleep is not so
/// even: Windows raises <c>SystemEvents.PowerModeChanged</c>, and macOS only tells a process that has registered for
/// <c>NSWorkspace</c> notifications — which a bare, unbundled binary cannot do. So the wake signal is derived instead,
/// by watching the wall clock run away from a monotonic timer; see <see cref="OnSleepPoll"/>. That works on both, needs
/// no interop, and is why this class no longer says "Windows" anywhere but in one <c>#if</c>.
/// </para>
/// <para>
/// It lives in the app rather than in Infrastructure because the sources are platform detail; <c>ConnectivityWatcher</c>
/// — everything that decides what to DO with a signal — is in Infrastructure behind <see cref="IConnectivitySignals"/>
/// and is tested there with a hand-driven source.
/// </para>
/// <para>
/// Every event is raised on an operating-system or timer thread, so subscribers must be quick and must not throw; the
/// watcher guarantees both. On Windows, subscribing to <c>SystemEvents</c> keeps a strong reference to this instance
/// for the life of the process, which is why <see cref="Dispose"/> matters even at shutdown.
/// </para>
/// </summary>
public sealed class SystemConnectivitySignals : IConnectivitySignals, IDisposable
{
    /// <summary>How often the derived wake detector looks. Frequent enough to reconnect promptly, rare enough to be free.</summary>
    private static readonly TimeSpan SleepPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far the wall clock may run past the timer before it is read as "the machine was asleep". Generous: a busy
    /// machine, a stopped debugger or a thread-pool starvation spike can all delay a timer by a second or two, and a
    /// false wake costs a needless reconnect.
    /// </summary>
    private static readonly TimeSpan SleepThreshold = TimeSpan.FromSeconds(20);

    private readonly ILogger<SystemConnectivitySignals> _logger;
    private readonly object _gate = new();
    private Timer? _sleepPoll;
    private long _lastPollTimestamp;
    private bool _subscribed;
    private bool _disposed;

    public SystemConnectivitySignals(ILogger<SystemConnectivitySignals> logger) => _logger = logger;

    public event Action<ConnectivitySignal>? Signalled;

    /// <summary>Starts listening.</summary>
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
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
#endif

            if (!OperatingSystem.IsWindows())
            {
                _lastPollTimestamp = Stopwatch.GetTimestamp();
                _sleepPoll = new Timer(OnSleepPoll, null, SleepPollInterval, SleepPollInterval);
            }

            _logger.LogInformation("Listening for power-resume and network-address-change events");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            // Losing these events costs a faster reconnect, not correctness: the backoff still gets there.
            _logger.LogWarning(ex, "Power and network change events are unavailable; reconnects will wait out the backoff");
        }
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Raise(ConnectivitySignal.PowerResumed);
        }
    }
#endif

    /// <summary>
    /// The derived wake signal. A timer set for five seconds that has not fired for a minute did not run slowly — the
    /// machine was suspended, and every socket it held is now stale whether or not it says so.
    /// </summary>
    private void OnSleepPoll(object? state)
    {
        var previous = Interlocked.Exchange(ref _lastPollTimestamp, Stopwatch.GetTimestamp());
        var elapsed = Stopwatch.GetElapsedTime(previous);
        if (elapsed >= SleepThreshold)
        {
            _logger.LogInformation(
                "{Elapsed:0.0}s passed between two {Interval:0.0}s polls: treating it as a wake from sleep",
                elapsed.TotalSeconds,
                SleepPollInterval.TotalSeconds);
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

        _sleepPoll?.Dispose();
        _sleepPoll = null;

        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
#endif
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug("Unsubscribing from the power/network events failed: {Message}", ex.Message);
        }
    }
}
