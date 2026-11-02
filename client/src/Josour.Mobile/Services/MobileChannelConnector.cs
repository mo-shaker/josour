using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Settings;
using Josour.Mobile.Platform;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.Services;

/// <summary>
/// The phone's counterpart of the desktop's <c>ControlChannelConnector</c>: opens the control channel once signed in (with the
/// phone's diagnostics), closes it on sign-out, keeps the last <c>hello.ack</c> (the public address the server sees) and signs
/// out when the server revokes the device. Reconnecting and re-announcing availability belong to <c>ControlChannel</c>.
/// </summary>
public sealed class MobileChannelConnector : IDisposable
{
    private readonly IControlChannel _channel;
    private readonly IAuthSession _auth;
    private readonly IDeviceInfoProvider _device;
    private readonly IAppSettingsStore _settings;
    private readonly INetworkFacts _network;
    private readonly ILogger<MobileChannelConnector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MobileChannelConnector(
        IControlChannel channel,
        IAuthSession auth,
        IDeviceInfoProvider device,
        IAppSettingsStore settings,
        INetworkFacts network,
        ILogger<MobileChannelConnector> logger)
    {
        _channel = channel;
        _auth = auth;
        _device = device;
        _settings = settings;
        _network = network;
        _logger = logger;
        _channel.MessageReceived += OnMessage;
        _channel.Closed += OnClosed;
    }

    /// <summary>The last <c>hello.ack</c>; null while disconnected.</summary>
    public HelloAckMessage? LastHello { get; private set; }

    /// <summary>Why the channel stopped for good, or null while it is connected or retrying.</summary>
    public ControlChannelClosed? LastClose { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="LastHello"/> or <see cref="LastClose"/> changed.</summary>
    public event Action? Changed;

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
            var facts = _network.Current;
            var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["os_build"] = _device.OsBuild,
                ["network"] = facts.Kind.ToString().ToLowerInvariant(),
                ["vpn_active"] = facts.IsVpn,
            };
            var hello = await _channel.ConnectAsync(token, deviceId.Value, diagnostics, ct).ConfigureAwait(false);
            LastHello = hello;
            _logger.LogInformation(
                "Control channel connected on {Network}: public_ip={PublicIp} max_session_minutes={MaxMinutes} allowed_ports={AllowedPorts}",
                facts.Kind,
                hello.PublicIp,
                hello.Settings.MaxSessionMinutes,
                string.Join(",", hello.Settings.AllowedPorts));
            RaiseChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

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

    private void OnMessage(ControlMessage message)
    {
        if (message is HelloAckMessage ack)
        {
            // A reconnect brings a fresh ack: after a move from Wi-Fi to mobile data the public address is a new one.
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await _auth.ForceSignOutAsync(SignOutReason.DeviceRevoked, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Could not end the local session after the control channel closed");
                }
            });
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
