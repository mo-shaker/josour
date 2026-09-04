using System.Net.Http;
using Microsoft.Extensions.Logging;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Device;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.App.Services;

/// <summary>
/// Decides WHEN the app wants to be connected: opens the control channel after sign-in (<c>hello</c> with a fresh access token,
/// the device id and the local diagnostics) and closes it on sign-out. Reconnection, heart-beat and re-announcing "available"
/// belong to <c>ControlChannel</c> itself.
/// <para>
/// It also owns the two things the ViewModels need from the channel's edges: the last <c>hello.ack</c> (server settings such as
/// <c>max_session_minutes</c>, refreshed on every reconnect) and the terminal close reason (4401/4403/4409). A <c>4403</c> ends
/// the local session so the sign-in window comes back with the right notice.
/// </para>
/// </summary>
public sealed class ControlChannelConnector : IDisposable
{
    private readonly IControlChannel _channel;
    private readonly IAuthSession _auth;
    private readonly IDeviceInfoProvider _device;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger<ControlChannelConnector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ControlChannelConnector(
        IControlChannel channel,
        IAuthSession auth,
        IDeviceInfoProvider device,
        IAppSettingsStore settings,
        ILogger<ControlChannelConnector> logger)
    {
        _channel = channel;
        _auth = auth;
        _device = device;
        _settings = settings;
        _logger = logger;
        _channel.MessageReceived += OnMessage;
        _channel.Closed += OnClosed;
    }

    /// <summary>The last <c>hello.ack</c> (server time, public IP, settings, allow-list version); null while disconnected.</summary>
    public HelloAckMessage? LastHello { get; private set; }

    /// <summary>Why the channel stopped for good, or null while it is connected / retrying.</summary>
    public ControlChannelClosed? LastClose { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="LastHello"/> or <see cref="LastClose"/> changed.</summary>
    public event Action? Changed;

    /// <summary>True when a server address is configured and the user is signed in, i.e. there is something to connect with.</summary>
    public bool CanConnect => _auth.IsSignedIn
        && _auth.DeviceId is not null
        && ServerUrl.TryNormalize(_settings.Current.ServerUrl, out var baseUri)
        && ServerUrl.IsAllowed(baseUri);

    /// <summary>Connects if signed in and not already connected. Returns false when there is nothing to connect with.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel.State is ControlChannelState.Connected or ControlChannelState.Connecting or ControlChannelState.Reconnecting)
            {
                return true;
            }

            if (!ServerUrl.TryNormalize(_settings.Current.ServerUrl, out var baseUri) || !ServerUrl.IsAllowed(baseUri))
            {
                _logger.LogInformation("Control channel not opened: no server address is configured");
                return false;
            }

            var token = await _auth.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            var deviceId = _auth.DeviceId;
            if (token is null || deviceId is null)
            {
                _logger.LogInformation("Control channel not opened: not signed in");
                return false;
            }

            LastClose = null;
            var hello = await _channel.ConnectAsync(token, deviceId.Value, BuildDiagnostics(baseUri), ct).ConfigureAwait(false);
            LastHello = hello;
            _logger.LogInformation(
                "Control channel connected: public_ip={PublicIp} server_time={ServerTime:O} max_session_minutes={MaxMinutes} request_timeout_seconds={RequestTimeout} allowed_ports={AllowedPorts} allowlist_version={AllowlistVersion}",
                hello.PublicIp,
                hello.ServerTime,
                hello.Settings.MaxSessionMinutes,
                hello.Settings.RequestTimeoutSeconds,
                string.Join(",", hello.Settings.AllowedPorts),
                hello.AllowlistVersion);
            RaiseChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the channel (idempotent). A later <see cref="ConnectAsync"/> opens a fresh connection.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_channel.State == ControlChannelState.Disconnected && LastHello is null)
            {
                return;
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
            LastHello = null;
            _logger.LogInformation("Control channel closed");
            RaiseChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the control channel failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What the server may use to explain a failed session later (docs/ws-protocol.md <c>hello.diagnostics</c>). No secrets.</summary>
    private Dictionary<string, object?> BuildDiagnostics(Uri serverUri) => new()
    {
        ["os_build"] = _device.OsBuild,
        ["system_proxy_present"] = HasSystemProxy(serverUri),

        // WEEK 5: firewall_rule_present / firewall_profile (FirewallRuleChecker), vpn_adapter (VpnAdapterDetector),
        //         ipv6_global (CandidateGatherer) — see docs/ws-protocol.md hello.diagnostics.
    };

    private bool HasSystemProxy(Uri serverUri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return proxy.GetProxy(serverUri) is not null;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug("Could not read the system proxy configuration: {Message}", ex.Message);
            return false;
        }
    }

    private void OnMessage(ControlMessage message)
    {
        if (message is HelloAckMessage ack)
        {
            // A reconnect brings a fresh ack (settings and allow-list version may have changed).
            LastHello = ack;
            RaiseChanged();
        }
    }

    private void OnClosed(ControlChannelClosed closed)
    {
        LastClose = closed;
        LastHello = null;
        _logger.LogWarning("Control channel closed for good: {Reason} (code {CloseCode})", closed.Reason, closed.CloseCode);
        RaiseChanged();

        if (closed.Reason == ControlCloseReason.DeviceRevoked)
        {
            // The server will not take this device back; end the local session so the sign-in window explains why.
            // On the thread pool: signing out disposes the channel, and this handler runs on the channel's own loop.
            _ = Task.Run(() => SignOutAsync(SignOutReason.DeviceRevoked));
        }
    }

    private async Task SignOutAsync(SignOutReason reason)
    {
        try
        {
            await _auth.ForceSignOutAsync(reason, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not end the local session after the control channel closed");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A control-channel Changed handler threw");
        }
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessage;
        _channel.Closed -= OnClosed;
        _gate.Dispose();
    }
}
