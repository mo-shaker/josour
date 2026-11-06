namespace Josour.Mobile.ViewModels;

/// <summary>
/// The one answer the Share tab gives first: can a request reach this phone right now, and if not, what to do. Computed by
/// <see cref="HostViewModel"/> from facts it already has, in this order of precedence (docs/design/mobile-phase2-mockups.html,
/// "Which hero state wins").
/// </summary>
public enum HostStatus
{
    /// <summary>"Available" is off.</summary>
    Off,

    /// <summary>Available, and the connection to the server is being made, confirmed, or was lost less than a moment ago.</summary>
    Connecting,

    /// <summary>Available, connected and confirmed by the server, with notifications and background use allowed.</summary>
    Ready,

    /// <summary>Available, but the server has been out of reach for longer than <see cref="HostViewModel.UnreachableGrace"/>.</summary>
    Unreachable,

    /// <summary>Available, but the phone has no network at all.</summary>
    NoNetwork,

    /// <summary>Reachable, but notifications are off: a request in the background would go unnoticed.</summary>
    NeedsNotifications,

    /// <summary>Reachable, but battery optimisation may stop the app in the background.</summary>
    NeedsBackground,

    /// <summary>A session is being prepared, connected, running or closed.</summary>
    InSession,

    /// <summary>The last session has ended and its summary has not been dismissed.</summary>
    SessionEnded,
}

/// <summary>The drawing the arch motif shows (<c>Controls.Motif</c>).</summary>
public enum MotifState
{
    Off, Connecting, Ready, Unreachable, Attention, Session, Ended, Empty, Offer,
}
