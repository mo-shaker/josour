using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Control;

namespace Josour.Infrastructure.Control;

/// <summary>What the operating system told us; both mean "the network you failed on may not be the network you have now".</summary>
public enum ConnectivitySignal
{
    /// <summary>The machine came back from sleep or hibernation (<c>SystemEvents.PowerModeChanged</c> / Resume).</summary>
    PowerResumed,

    /// <summary>An interface gained or lost an address (<c>NetworkChange.NetworkAddressChanged</c>): Wi-Fi, VPN, docking.</summary>
    NetworkAddressChanged,
}

/// <summary>
/// The source of <see cref="ConnectivitySignal"/>s. The Windows implementation lives in the app (it needs
/// <c>Microsoft.Win32.SystemEvents</c>, which only exists in the desktop framework); tests raise the events by hand.
/// </summary>
public interface IConnectivitySignals
{
    /// <summary>Raised on an operating-system thread; handlers must be quick and must not throw.</summary>
    event Action<ConnectivitySignal>? Signalled;
}

/// <summary>
/// Turns "the machine woke up" and "the network changed" into an immediate re-evaluation of the control channel
/// (plan 8.5: نوم الجهاز / تغيير الشبكة).
/// <para>
/// Two things happen on a signal. A channel that is reconnecting is told to stop waiting out its backoff
/// (<see cref="IReconnectNow"/>), because the 30 s it still owes were budgeted for a server that is down, not for a
/// laptop that just opened its lid; a channel that thinks it is connected pings at once, which is how a socket that did
/// not survive the sleep is discovered now instead of at the next interval. And a channel that is fully disconnected —
/// signed in but with nothing running, e.g. after the app gave up — is asked to connect again through
/// <see cref="ConnectivityWatcherOptions.Reconnect"/>.
/// </para>
/// <para>
/// What deliberately does NOT happen here is anything about the session: a session that was live when the machine slept
/// is torn down by <c>SessionCoordinator</c> the moment the channel leaves <c>Connected</c>, and the server has already
/// ended it with <c>host_disconnected</c>/<c>guest_disconnected</c> (docs/ws-protocol.md section 1). Resuming therefore
/// reconnects into a clean, session-less state and the panel shows why it ended.
/// </para>
/// <para>
/// Signals arrive in bursts (a resume is usually followed by several address changes), so they are coalesced: one wake
/// per <see cref="ConnectivityWatcherOptions.Quiet"/> window at most.
/// </para>
/// </summary>
public sealed class ConnectivityWatcher : IDisposable
{
    private readonly IControlChannel _channel;
    private readonly IConnectivitySignals _signals;
    private readonly ConnectivityWatcherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private long _lastSignal;
    private bool _seenAny;
    private bool _disposed;

    public ConnectivityWatcher(
        IControlChannel channel,
        IConnectivitySignals signals,
        ILogger<ConnectivityWatcher>? logger = null,
        TimeProvider? time = null,
        ConnectivityWatcherOptions? options = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _options = options ?? ConnectivityWatcherOptions.Default;

        _signals.Signalled += OnSignal;
    }

    /// <summary>How many signals were acted on (the coalesced ones do not count); the tests and the log read it.</summary>
    public int Handled { get; private set; }

    private void OnSignal(ConnectivitySignal signal)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var now = _time.GetTimestamp();
                if (_seenAny && _time.GetElapsedTime(_lastSignal, now) < _options.Quiet)
                {
                    _logger.LogDebug("Ignoring {Signal}: another connectivity signal was handled less than {Quiet:0.#} s ago", signal, _options.Quiet.TotalSeconds);
                    return;
                }

                _lastSignal = now;
                _seenAny = true;
                Handled++;
            }

            var state = _channel.State;
            _logger.LogInformation("{Signal} while the control channel is {State}: re-evaluating the connection", signal, state);

            if (_channel is IReconnectNow wakeable)
            {
                wakeable.ReconnectNow(Describe(signal));
            }
            else
            {
                _logger.LogDebug("The control channel cannot be woken; only the reconnect callback applies");
            }

            if (state == ControlChannelState.Disconnected && _options.Reconnect is { } reconnect)
            {
                // Nothing is running to be woken: this is the "the app gave up / was never connected" case.
                _ = SafeReconnectAsync(reconnect);
            }
        }
        catch (Exception ex)
        {
            // The handler runs on an OS callback thread; an exception there would take the process down.
            _logger.LogError(ex, "Handling the {Signal} connectivity signal failed", signal);
        }
    }

    private async Task SafeReconnectAsync(Func<CancellationToken, Task> reconnect)
    {
        try
        {
            await reconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconnecting the control channel after a connectivity signal failed");
        }
    }

    private static string Describe(ConnectivitySignal signal) => signal switch
    {
        ConnectivitySignal.PowerResumed => "the machine resumed from sleep",
        _ => "a network address changed",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _signals.Signalled -= OnSignal;
    }
}

/// <summary>Knobs of <see cref="ConnectivityWatcher"/>.</summary>
public sealed record ConnectivityWatcherOptions
{
    public static ConnectivityWatcherOptions Default { get; } = new();

    /// <summary>Signals closer together than this are one event (a resume is followed by a burst of address changes).</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Opens the channel when it is not running at all (the app wires <c>ControlChannelConnector.ConnectAsync</c>, which
    /// checks "signed in and a server address is configured" first). Null leaves that case alone.
    /// </summary>
    public Func<CancellationToken, Task>? Reconnect { get; init; }
}
