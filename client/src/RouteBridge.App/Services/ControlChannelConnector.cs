using Microsoft.Extensions.Logging;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Device;

namespace RouteBridge.App.Services;

/// <summary>
/// Opens the control channel after sign-in (<c>hello</c> with a fresh access token + device id) and closes it on sign-out.
/// Reconnection, heart-beat and re-announcing "available" belong to the channel itself (week 3's real ControlChannel);
/// this class only decides WHEN the app wants to be connected.
/// </summary>
public sealed class ControlChannelConnector
{
    private readonly IControlChannel _channel;
    private readonly IAuthSession _auth;
    private readonly IDeviceInfoProvider _device;
    private readonly ILogger<ControlChannelConnector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ControlChannelConnector(IControlChannel channel, IAuthSession auth, IDeviceInfoProvider device, ILogger<ControlChannelConnector> logger)
    {
        _channel = channel;
        _auth = auth;
        _device = device;
        _logger = logger;
    }

    /// <summary>The last <c>hello.ack</c> (server time, public IP, settings, allow-list version); null while disconnected.</summary>
    public HelloAckMessage? LastHello { get; private set; }

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

            var token = await _auth.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            var deviceId = _auth.DeviceId;
            if (token is null || deviceId is null)
            {
                _logger.LogInformation("Control channel not opened: not signed in");
                return false;
            }

            var diagnostics = new Dictionary<string, object?>
            {
                ["os_build"] = _device.OsBuild,
                // WEEK 3: firewall_rule_present / firewall_profile (FirewallRuleChecker), vpn_adapter (VpnAdapterDetector),
                //         system_proxy_present, ipv6_global (CandidateGatherer) — see docs/ws-protocol.md hello.diagnostics.
            };

            var hello = await _channel.ConnectAsync(token, deviceId.Value, diagnostics, ct).ConfigureAwait(false);
            LastHello = hello;
            _logger.LogInformation(
                "Control channel connected: public_ip={PublicIp} server_time={ServerTime:O} max_session_minutes={MaxMinutes} request_timeout_seconds={RequestTimeout} allowed_ports={AllowedPorts} allowlist_version={AllowlistVersion}",
                hello.PublicIp,
                hello.ServerTime,
                hello.Settings.MaxSessionMinutes,
                hello.Settings.RequestTimeoutSeconds,
                string.Join(",", hello.Settings.AllowedPorts),
                hello.AllowlistVersion);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the channel (idempotent). The mock and week 3's channel both allow a later <see cref="ConnectAsync"/>.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_channel.State == ControlChannelState.Disconnected)
            {
                return;
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
            LastHello = null;
            _logger.LogInformation("Control channel closed");
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
}
