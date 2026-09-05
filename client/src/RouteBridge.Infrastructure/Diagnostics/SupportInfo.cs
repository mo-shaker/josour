using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Device;
using RouteBridge.Infrastructure.Logging;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.Infrastructure.Diagnostics;

/// <summary>
/// The facts a support conversation needs to start: which build, which machine, which server, and what the connection is
/// doing. Assembled here rather than in the About window so that the one rule it has to keep can be tested —
/// <b>nothing secret appears</b>.
/// <para>
/// The device <i>id</i> is here on purpose: it is the identifier an administrator sees in <c>GET /admin/devices</c> and the
/// only way to match a user's report to a row. The device <i>secret</i>, the access token and the refresh token are never
/// read by this type at all, which is stronger than filtering them out afterwards.
/// </para>
/// </summary>
/// <param name="AppVersion">e.g. <c>0.1.0</c>.</param>
/// <param name="DeviceName">Machine name as the server knows it.</param>
/// <param name="DeviceId">The server's device id, or empty while signed out.</param>
/// <param name="OsVersion">Marketing OS name.</param>
/// <param name="OsBuild">OS build.</param>
/// <param name="ServerUrl">The configured server address, or empty when there is none.</param>
/// <param name="SignedInAs">The signed-in user's e-mail, or empty while signed out.</param>
/// <param name="ConnectionState">The control channel's state as the caller words it.</param>
/// <param name="LogDirectory">Where the log files are, so the user can be pointed at them.</param>
public sealed record SupportInfo(
    string AppVersion,
    string DeviceName,
    string DeviceId,
    string OsVersion,
    string OsBuild,
    string ServerUrl,
    string SignedInAs,
    string ConnectionState,
    string LogDirectory)
{
    /// <summary>
    /// Builds the snapshot. <paramref name="auth"/> is read for the device id and the e-mail only; no token property is
    /// touched, so a future edit that wanted to show one would have to add the call deliberately.
    /// </summary>
    public static SupportInfo Build(
        IDeviceInfoProvider device,
        IAuthSession? auth,
        AppSettings? settings,
        string connectionState,
        string? logDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var info = device.GetDeviceInfo();
        return new SupportInfo(
            info.AppVersion,
            info.DeviceName,
            auth?.DeviceId?.ToString() ?? string.Empty,
            info.OsVersion,
            info.OsBuild,
            settings?.ServerUrl ?? string.Empty,
            auth?.CurrentUser?.Email ?? string.Empty,
            connectionState ?? string.Empty,
            logDirectory ?? LoggingSetup.DefaultLogDirectory);
    }

    /// <summary>The same values as label/value pairs, for a "copy" button and for the log line at start-up.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToPairs() => new[]
    {
        new KeyValuePair<string, string>("app_version", AppVersion),
        new KeyValuePair<string, string>("device_name", DeviceName),
        new KeyValuePair<string, string>("device_id", DeviceId),
        new KeyValuePair<string, string>("os_version", OsVersion),
        new KeyValuePair<string, string>("os_build", OsBuild),
        new KeyValuePair<string, string>("server_url", ServerUrl),
        new KeyValuePair<string, string>("signed_in_as", SignedInAs),
        new KeyValuePair<string, string>("connection", ConnectionState),
        new KeyValuePair<string, string>("logs", LogDirectory),
    };

    /// <summary>One block of text the user can paste into a support message.</summary>
    public string ToText() => string.Join(Environment.NewLine, ToPairs().Select(p => $"{p.Key}: {p.Value}"));
}
