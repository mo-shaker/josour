using Josour.Core.Control;

namespace Josour.App.Services;

/// <summary>
/// The one sentence that describes the control channel, shared by the status line, the tray tooltip and the About window.
/// It lives here rather than on a view model because week 6 gave it a second reader, and a support conversation that
/// quotes a different word than the status bar is worse than no word at all.
/// </summary>
public static class ConnectionStatusText
{
    /// <summary>Live state first, then — only once it has stopped for good — why.</summary>
    /// <param name="state">The channel's current state.</param>
    /// <param name="lastClose">Why it closed for good, or null while it is connected or retrying.</param>
    /// <param name="canConnect">Whether there is a server address and a signed-in user to connect with.</param>
    public static string Describe(ControlChannelState state, ControlChannelClosed? lastClose, bool canConnect) => state switch
    {
        ControlChannelState.Connected => Strings.StatusConnected,
        ControlChannelState.Connecting => Strings.StatusConnecting,
        ControlChannelState.Reconnecting => Strings.StatusReconnecting,
        _ => lastClose?.Reason switch
        {
            ControlCloseReason.ReplacedByAnotherConnection => Strings.StatusReplacedElsewhere,
            ControlCloseReason.Unauthorized => Strings.StatusSignInExpired,
            ControlCloseReason.DeviceRevoked => Strings.SignedOutDeviceRevoked,
            ControlCloseReason.Failed when !canConnect => Strings.StatusNoServer,
            _ => Strings.StatusOffline,
        },
    };
}
