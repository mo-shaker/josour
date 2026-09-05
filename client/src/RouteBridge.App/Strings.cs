using RouteBridge.Infrastructure.Localization;

namespace RouteBridge.App;

/// <summary>
/// Every user-facing string of the app, resolved from the language chosen at start-up
/// (<see cref="LocalizedStrings.Current"/>: Arabic by default, English with <c>--lang en</c> or the <c>language</c> setting).
/// <para>
/// The members stayed properties with the same names the whole UI already binds to with
/// <c>{x:Static app:Strings.X}</c>, so switching from constants to resources changed no call site. The text itself lives in
/// <c>RouteBridge.Infrastructure/Localization/StringsAr.resx</c> and <c>StringsEn.resx</c>; keep NEW strings there and add
/// the key to <see cref="UiStringKeys"/> — never a literal in XAML or code-behind.
/// </para>
/// <para>
/// The language is fixed before the first window is created and never changes while the app runs, so a static property that
/// reads the current pack is enough (WPF evaluates <c>x:Static</c> once, at load time).
/// </para>
/// </summary>
public static class Strings
{
    // ---- product ----
    public static string AppName => LocalizedStrings.Current[UiStringKeys.AppName];
    public static string VersionFormat => LocalizedStrings.Current[UiStringKeys.VersionFormat];

    // ---- tray ----
    public static string TrayTooltip => LocalizedStrings.Current[UiStringKeys.TrayTooltip];
    public static string TrayShowWindow => LocalizedStrings.Current[UiStringKeys.TrayShowWindow];
    public static string TrayAvailableForRequests => LocalizedStrings.Current[UiStringKeys.TrayAvailableForRequests];
    public static string TrayStartWithWindows => LocalizedStrings.Current[UiStringKeys.TrayStartWithWindows];
    public static string TrayDebug => LocalizedStrings.Current[UiStringKeys.TrayDebug];
    public static string TraySimulateIncomingRequest => LocalizedStrings.Current[UiStringKeys.TraySimulateIncomingRequest];
    public static string TraySignOut => LocalizedStrings.Current[UiStringKeys.TraySignOut];
    public static string TrayExit => LocalizedStrings.Current[UiStringKeys.TrayExit];
    public static string TrayTooltipSignedInFormat => LocalizedStrings.Current[UiStringKeys.TrayTooltipSignedInFormat];
    public static string TrayTooltipNotSignedIn => LocalizedStrings.Current[UiStringKeys.TrayTooltipNotSignedIn];

    // ---- main window ----
    public static string HostTab => LocalizedStrings.Current[UiStringKeys.HostTab];
    public static string GuestTab => LocalizedStrings.Current[UiStringKeys.GuestTab];
    public static string StatusNotSignedIn => LocalizedStrings.Current[UiStringKeys.StatusNotSignedIn];
    public static string StatusSignedInFormat => LocalizedStrings.Current[UiStringKeys.StatusSignedInFormat];
    public static string StatusOffline => LocalizedStrings.Current[UiStringKeys.StatusOffline];
    public static string StatusConnecting => LocalizedStrings.Current[UiStringKeys.StatusConnecting];
    public static string StatusConnected => LocalizedStrings.Current[UiStringKeys.StatusConnected];
    public static string StatusReconnecting => LocalizedStrings.Current[UiStringKeys.StatusReconnecting];
    public static string StatusReplacedElsewhere => LocalizedStrings.Current[UiStringKeys.StatusReplacedElsewhere];
    public static string StatusSignInExpired => LocalizedStrings.Current[UiStringKeys.StatusSignInExpired];
    public static string StatusNoServer => LocalizedStrings.Current[UiStringKeys.StatusNoServer];

    // ---- host page ----
    public static string HostPageTitle => LocalizedStrings.Current[UiStringKeys.HostPageTitle];
    public static string HostPageDescription => LocalizedStrings.Current[UiStringKeys.HostPageDescription];
    public static string HostAvailableToggle => LocalizedStrings.Current[UiStringKeys.HostAvailableToggle];
    public static string HostStatusAvailable => LocalizedStrings.Current[UiStringKeys.HostStatusAvailable];
    public static string HostStatusNotAvailable => LocalizedStrings.Current[UiStringKeys.HostStatusNotAvailable];
    public static string HostStatusAnnouncing => LocalizedStrings.Current[UiStringKeys.HostStatusAnnouncing];
    public static string HostStatusAvailabilityFailed => LocalizedStrings.Current[UiStringKeys.HostStatusAvailabilityFailed];
    public static string HostNotConnectedTitle => LocalizedStrings.Current[UiStringKeys.HostNotConnectedTitle];
    public static string HostNotConnectedHint => LocalizedStrings.Current[UiStringKeys.HostNotConnectedHint];
    public static string HostFirewallWarningTitle => LocalizedStrings.Current[UiStringKeys.HostFirewallWarningTitle];
    public static string HostFirewallWarningMessage => LocalizedStrings.Current[UiStringKeys.HostFirewallWarningMessage];
    public static string HostSimulatedServerTitle => LocalizedStrings.Current[UiStringKeys.HostSimulatedServerTitle];
    public static string HostSimulatedServerHint => LocalizedStrings.Current[UiStringKeys.HostSimulatedServerHint];

    // ---- guest page ----
    public static string GuestPageTitle => LocalizedStrings.Current[UiStringKeys.GuestPageTitle];
    public static string GuestPageDescription => LocalizedStrings.Current[UiStringKeys.GuestPageDescription];
    public static string GuestRefresh => LocalizedStrings.Current[UiStringKeys.GuestRefresh];
    public static string GuestNoHostsTitle => LocalizedStrings.Current[UiStringKeys.GuestNoHostsTitle];
    public static string GuestNoHostsText => LocalizedStrings.Current[UiStringKeys.GuestNoHostsText];
    public static string HostReachable => LocalizedStrings.Current[UiStringKeys.HostReachable];
    public static string HostUnreachable => LocalizedStrings.Current[UiStringKeys.HostUnreachable];
    public static string HostReachabilityUnknown => LocalizedStrings.Current[UiStringKeys.HostReachabilityUnknown];
    public static string GuestHostsUnavailableTitle => LocalizedStrings.Current[UiStringKeys.GuestHostsUnavailableTitle];
    public static string GuestHostsUnavailableText => LocalizedStrings.Current[UiStringKeys.GuestHostsUnavailableText];
    public static string GuestHostsErrorFormat => LocalizedStrings.Current[UiStringKeys.GuestHostsErrorFormat];
    public static string GuestNotSignedInText => LocalizedStrings.Current[UiStringKeys.GuestNotSignedInText];
    public static string GuestDurationLabel => LocalizedStrings.Current[UiStringKeys.GuestDurationLabel];
    public static string GuestRequestConnection => LocalizedStrings.Current[UiStringKeys.GuestRequestConnection];
    public static string GuestWaitingTitle => LocalizedStrings.Current[UiStringKeys.GuestWaitingTitle];
    public static string GuestWaitingTextFormat => LocalizedStrings.Current[UiStringKeys.GuestWaitingTextFormat];
    public static string GuestCancelRequest => LocalizedStrings.Current[UiStringKeys.GuestCancelRequest];
    public static string GuestRequestSentFormat => LocalizedStrings.Current[UiStringKeys.GuestRequestSentFormat];
    public static string GuestRequestAccepted => LocalizedStrings.Current[UiStringKeys.GuestRequestAccepted];
    public static string GuestRequestRejected => LocalizedStrings.Current[UiStringKeys.GuestRequestRejected];
    public static string GuestRequestExpiredText => LocalizedStrings.Current[UiStringKeys.GuestRequestExpiredText];
    public static string GuestRequestCancelled => LocalizedStrings.Current[UiStringKeys.GuestRequestCancelled];
    public static string GuestRequestDisconnected => LocalizedStrings.Current[UiStringKeys.GuestRequestDisconnected];
    public static string GuestErrorHostUnavailable => LocalizedStrings.Current[UiStringKeys.GuestErrorHostUnavailable];
    public static string GuestErrorSessionExists => LocalizedStrings.Current[UiStringKeys.GuestErrorSessionExists];
    public static string GuestErrorRequestPending => LocalizedStrings.Current[UiStringKeys.GuestErrorRequestPending];
    public static string GuestErrorRateLimited => LocalizedStrings.Current[UiStringKeys.GuestErrorRateLimited];
    public static string GuestErrorNotConnected => LocalizedStrings.Current[UiStringKeys.GuestErrorNotConnected];
    public static string GuestErrorGenericFormat => LocalizedStrings.Current[UiStringKeys.GuestErrorGenericFormat];

    // ---- session panel ----
    public static string SessionPanelTitle => LocalizedStrings.Current[UiStringKeys.SessionPanelTitle];
    public static string SessionPeerFormat => LocalizedStrings.Current[UiStringKeys.SessionPeerFormat];
    public static string SessionRoleHost => LocalizedStrings.Current[UiStringKeys.SessionRoleHost];
    public static string SessionRoleGuest => LocalizedStrings.Current[UiStringKeys.SessionRoleGuest];
    public static string SessionPhasePreparing => LocalizedStrings.Current[UiStringKeys.SessionPhasePreparing];
    public static string SessionPhaseConnecting => LocalizedStrings.Current[UiStringKeys.SessionPhaseConnecting];
    public static string SessionPhaseActive => LocalizedStrings.Current[UiStringKeys.SessionPhaseActive];
    public static string SessionPhaseEnding => LocalizedStrings.Current[UiStringKeys.SessionPhaseEnding];
    public static string SessionPhaseEnded => LocalizedStrings.Current[UiStringKeys.SessionPhaseEnded];
    public static string SessionTimeRemainingFormat => LocalizedStrings.Current[UiStringKeys.SessionTimeRemainingFormat];
    public static string SessionTimeUp => LocalizedStrings.Current[UiStringKeys.SessionTimeUp];
    public static string SessionDataUsedFormat => LocalizedStrings.Current[UiStringKeys.SessionDataUsedFormat];
    public static string SessionDisconnect => LocalizedStrings.Current[UiStringKeys.SessionDisconnect];
    public static string SessionReopenBrowser => LocalizedStrings.Current[UiStringKeys.SessionReopenBrowser];
    public static string SessionDismiss => LocalizedStrings.Current[UiStringKeys.SessionDismiss];
    public static string SessionSummaryFormat => LocalizedStrings.Current[UiStringKeys.SessionSummaryFormat];
    public static string BytesFormat => LocalizedStrings.Current[UiStringKeys.BytesFormat];
    public static string BytesUnitB => LocalizedStrings.Current[UiStringKeys.BytesUnitB];
    public static string BytesUnitKb => LocalizedStrings.Current[UiStringKeys.BytesUnitKb];
    public static string BytesUnitMb => LocalizedStrings.Current[UiStringKeys.BytesUnitMb];
    public static string BytesUnitGb => LocalizedStrings.Current[UiStringKeys.BytesUnitGb];

    // ---- session end reasons (docs/ws-protocol.md section 5) ----
    public static string SessionEndedByYou => LocalizedStrings.Current[UiStringKeys.SessionEndedByYou];
    public static string SessionEndedByPeer => LocalizedStrings.Current[UiStringKeys.SessionEndedByPeer];
    public static string SessionEndedExpired => LocalizedStrings.Current[UiStringKeys.SessionEndedExpired];
    public static string SessionEndedPeerDisconnected => LocalizedStrings.Current[UiStringKeys.SessionEndedPeerDisconnected];
    public static string SessionEndedYouDisconnected => LocalizedStrings.Current[UiStringKeys.SessionEndedYouDisconnected];
    public static string SessionEndedConnectFailed => LocalizedStrings.Current[UiStringKeys.SessionEndedConnectFailed];
    public static string SessionEndedAdminTerminated => LocalizedStrings.Current[UiStringKeys.SessionEndedAdminTerminated];
    public static string SessionEndedBrowserNotProxied => LocalizedStrings.Current[UiStringKeys.SessionEndedBrowserNotProxied];
    public static string SessionEndedProtocolError => LocalizedStrings.Current[UiStringKeys.SessionEndedProtocolError];
    public static string SessionEndedUnknownFormat => LocalizedStrings.Current[UiStringKeys.SessionEndedUnknownFormat];

    // ---- work browser ----
    public static string BrowserErrorNotFound => LocalizedStrings.Current[UiStringKeys.BrowserErrorNotFound];
    public static string BrowserErrorManagedByPolicy => LocalizedStrings.Current[UiStringKeys.BrowserErrorManagedByPolicy];
    public static string BrowserErrorInstanceHandoff => LocalizedStrings.Current[UiStringKeys.BrowserErrorInstanceHandoff];
    public static string BrowserErrorProbeTimeout => LocalizedStrings.Current[UiStringKeys.BrowserErrorProbeTimeout];
    public static string BrowserErrorOther => LocalizedStrings.Current[UiStringKeys.BrowserErrorOther];

    // ---- incoming request: the pre-accept disclosure (product document section 15) ----
    public static string IncomingRequestWindowTitle => LocalizedStrings.Current[UiStringKeys.IncomingRequestWindowTitle];
    public static string IncomingRequestHeadingFormat => LocalizedStrings.Current[UiStringKeys.IncomingRequestHeadingFormat];
    public static string IncomingRequestRequesterLabel => LocalizedStrings.Current[UiStringKeys.IncomingRequestRequesterLabel];
    public static string IncomingRequestDeviceLabel => LocalizedStrings.Current[UiStringKeys.IncomingRequestDeviceLabel];
    public static string IncomingRequestDurationLabel => LocalizedStrings.Current[UiStringKeys.IncomingRequestDurationLabel];
    public static string IncomingRequestAllowedSitesLabel => LocalizedStrings.Current[UiStringKeys.IncomingRequestAllowedSitesLabel];
    public static string DurationMinutesFormat => LocalizedStrings.Current[UiStringKeys.DurationMinutesFormat];
    public static string AllowedSitesNote => LocalizedStrings.Current[UiStringKeys.AllowedSitesNote];
    public static string AllowedSitesSummaryFormat => LocalizedStrings.Current[UiStringKeys.AllowedSitesSummaryFormat];
    public static string AllowedSitesUnavailable => LocalizedStrings.Current[UiStringKeys.AllowedSitesUnavailable];
    public static string AllowedSitesEmpty => LocalizedStrings.Current[UiStringKeys.AllowedSitesEmpty];
    public static string IncomingRequestWarningTitle => LocalizedStrings.Current[UiStringKeys.IncomingRequestWarningTitle];
    public static string IncomingRequestWarning => LocalizedStrings.Current[UiStringKeys.IncomingRequestWarning];
    public static string IncomingRequestDisconnectHint => LocalizedStrings.Current[UiStringKeys.IncomingRequestDisconnectHint];
    public static string CountdownFormat => LocalizedStrings.Current[UiStringKeys.CountdownFormat];
    public static string RequestExpired => LocalizedStrings.Current[UiStringKeys.RequestExpired];
    public static string Accept => LocalizedStrings.Current[UiStringKeys.Accept];
    public static string Reject => LocalizedStrings.Current[UiStringKeys.Reject];

    // ---- toasts ----
    public static string ToastIncomingRequestTitle => LocalizedStrings.Current[UiStringKeys.ToastIncomingRequestTitle];
    public static string ToastIncomingRequestBodyFormat => LocalizedStrings.Current[UiStringKeys.ToastIncomingRequestBodyFormat];

    // ---- debug ----
    public static string DebugSampleGuestName => LocalizedStrings.Current[UiStringKeys.DebugSampleGuestName];
    public static string DebugSampleGuestDevice => LocalizedStrings.Current[UiStringKeys.DebugSampleGuestDevice];

    // ---- login ----
    public static string LoginWindowTitle => LocalizedStrings.Current[UiStringKeys.LoginWindowTitle];
    public static string LoginHeading => LocalizedStrings.Current[UiStringKeys.LoginHeading];
    public static string LoginDescription => LocalizedStrings.Current[UiStringKeys.LoginDescription];
    public static string LoginServerUrlLabel => LocalizedStrings.Current[UiStringKeys.LoginServerUrlLabel];
    public static string LoginServerUrlPlaceholder => LocalizedStrings.Current[UiStringKeys.LoginServerUrlPlaceholder];
    public static string LoginEmailLabel => LocalizedStrings.Current[UiStringKeys.LoginEmailLabel];
    public static string LoginPasswordLabel => LocalizedStrings.Current[UiStringKeys.LoginPasswordLabel];
    public static string LoginSignIn => LocalizedStrings.Current[UiStringKeys.LoginSignIn];
    public static string LoginSigningIn => LocalizedStrings.Current[UiStringKeys.LoginSigningIn];
    public static string LoginErrorServerUrl => LocalizedStrings.Current[UiStringKeys.LoginErrorServerUrl];
    public static string LoginErrorInvalidCredentials => LocalizedStrings.Current[UiStringKeys.LoginErrorInvalidCredentials];
    public static string LoginErrorAccountLocked => LocalizedStrings.Current[UiStringKeys.LoginErrorAccountLocked];
    public static string LoginErrorAccountDisabled => LocalizedStrings.Current[UiStringKeys.LoginErrorAccountDisabled];
    public static string LoginErrorDeviceRevoked => LocalizedStrings.Current[UiStringKeys.LoginErrorDeviceRevoked];
    public static string LoginErrorDeviceRejected => LocalizedStrings.Current[UiStringKeys.LoginErrorDeviceRejected];
    public static string LoginErrorRateLimited => LocalizedStrings.Current[UiStringKeys.LoginErrorRateLimited];
    public static string LoginErrorUnavailable => LocalizedStrings.Current[UiStringKeys.LoginErrorUnavailable];
    public static string LoginErrorValidationFormat => LocalizedStrings.Current[UiStringKeys.LoginErrorValidationFormat];
    public static string LoginErrorGenericFormat => LocalizedStrings.Current[UiStringKeys.LoginErrorGenericFormat];

    // ---- signed out notices ----
    public static string SignedOutTitle => LocalizedStrings.Current[UiStringKeys.SignedOutTitle];
    public static string SignedOutSessionExpired => LocalizedStrings.Current[UiStringKeys.SignedOutSessionExpired];
    public static string SignedOutDeviceRevoked => LocalizedStrings.Current[UiStringKeys.SignedOutDeviceRevoked];
    public static string SignedOutAccountDisabled => LocalizedStrings.Current[UiStringKeys.SignedOutAccountDisabled];

    // ---- host readiness: firewall + VPN (week 6) ----
    public static string HostVpnWarningTitle => LocalizedStrings.Current[UiStringKeys.HostVpnWarningTitle];
    public static string HostVpnWarningMessageFormat => LocalizedStrings.Current[UiStringKeys.HostVpnWarningMessageFormat];
    public static string ReadinessTitle => LocalizedStrings.Current[UiStringKeys.ReadinessTitle];
    public static string ReadinessChecking => LocalizedStrings.Current[UiStringKeys.ReadinessChecking];
    public static string ReadinessRecheck => LocalizedStrings.Current[UiStringKeys.ReadinessRecheck];
    public static string ReadinessFirewallOk => LocalizedStrings.Current[UiStringKeys.ReadinessFirewallOk];
    public static string ReadinessFirewallMissing => LocalizedStrings.Current[UiStringKeys.ReadinessFirewallMissing];
    public static string ReadinessFirewallUnknown => LocalizedStrings.Current[UiStringKeys.ReadinessFirewallUnknown];
    public static string ReadinessVpnOk => LocalizedStrings.Current[UiStringKeys.ReadinessVpnOk];
    public static string ReadinessVpnWarningFormat => LocalizedStrings.Current[UiStringKeys.ReadinessVpnWarningFormat];

    // ---- settings ----
    public static string SettingsWindowTitle => LocalizedStrings.Current[UiStringKeys.SettingsWindowTitle];
    public static string SettingsHeading => LocalizedStrings.Current[UiStringKeys.SettingsHeading];
    public static string SettingsDescription => LocalizedStrings.Current[UiStringKeys.SettingsDescription];
    public static string SettingsServerSection => LocalizedStrings.Current[UiStringKeys.SettingsServerSection];
    public static string SettingsServerUrlLabel => LocalizedStrings.Current[UiStringKeys.SettingsServerUrlLabel];
    public static string SettingsServerUrlHint => LocalizedStrings.Current[UiStringKeys.SettingsServerUrlHint];
    public static string SettingsTestConnection => LocalizedStrings.Current[UiStringKeys.SettingsTestConnection];
    public static string SettingsTesting => LocalizedStrings.Current[UiStringKeys.SettingsTesting];
    public static string SettingsServerOk => LocalizedStrings.Current[UiStringKeys.SettingsServerOk];
    public static string SettingsServerInvalidUrl => LocalizedStrings.Current[UiStringKeys.SettingsServerInvalidUrl];
    public static string SettingsServerInsecure => LocalizedStrings.Current[UiStringKeys.SettingsServerInsecure];
    public static string SettingsServerUnreachable => LocalizedStrings.Current[UiStringKeys.SettingsServerUnreachable];
    public static string SettingsServerNotRouteBridge => LocalizedStrings.Current[UiStringKeys.SettingsServerNotRouteBridge];
    public static string SettingsServerDetailFormat => LocalizedStrings.Current[UiStringKeys.SettingsServerDetailFormat];
    public static string SettingsInterfaceSection => LocalizedStrings.Current[UiStringKeys.SettingsInterfaceSection];
    public static string SettingsLanguageLabel => LocalizedStrings.Current[UiStringKeys.SettingsLanguageLabel];
    public static string SettingsLanguageArabic => LocalizedStrings.Current[UiStringKeys.SettingsLanguageArabic];
    public static string SettingsLanguageEnglish => LocalizedStrings.Current[UiStringKeys.SettingsLanguageEnglish];
    public static string SettingsLanguageRestartNote => LocalizedStrings.Current[UiStringKeys.SettingsLanguageRestartNote];
    public static string SettingsBrowserLabel => LocalizedStrings.Current[UiStringKeys.SettingsBrowserLabel];
    public static string SettingsBrowserHint => LocalizedStrings.Current[UiStringKeys.SettingsBrowserHint];
    public static string SettingsBrowserAuto => LocalizedStrings.Current[UiStringKeys.SettingsBrowserAuto];
    public static string SettingsBrowserChrome => LocalizedStrings.Current[UiStringKeys.SettingsBrowserChrome];
    public static string SettingsBrowserEdge => LocalizedStrings.Current[UiStringKeys.SettingsBrowserEdge];
    public static string SettingsStartupSection => LocalizedStrings.Current[UiStringKeys.SettingsStartupSection];
    public static string SettingsStartWithWindows => LocalizedStrings.Current[UiStringKeys.SettingsStartWithWindows];
    public static string SettingsStartWithWindowsUnavailable => LocalizedStrings.Current[UiStringKeys.SettingsStartWithWindowsUnavailable];
    public static string SettingsStartMinimized => LocalizedStrings.Current[UiStringKeys.SettingsStartMinimized];
    public static string SettingsSave => LocalizedStrings.Current[UiStringKeys.SettingsSave];
    public static string SettingsCancel => LocalizedStrings.Current[UiStringKeys.SettingsCancel];
    public static string SettingsSaved => LocalizedStrings.Current[UiStringKeys.SettingsSaved];
    public static string SettingsSaveFailedFormat => LocalizedStrings.Current[UiStringKeys.SettingsSaveFailedFormat];

    // ---- first run ----
    public static string FirstRunWindowTitle => LocalizedStrings.Current[UiStringKeys.FirstRunWindowTitle];
    public static string FirstRunWelcomeHeading => LocalizedStrings.Current[UiStringKeys.FirstRunWelcomeHeading];
    public static string FirstRunWelcomeText => LocalizedStrings.Current[UiStringKeys.FirstRunWelcomeText];
    public static string FirstRunStepFormat => LocalizedStrings.Current[UiStringKeys.FirstRunStepFormat];
    public static string FirstRunServerTitle => LocalizedStrings.Current[UiStringKeys.FirstRunServerTitle];
    public static string FirstRunServerText => LocalizedStrings.Current[UiStringKeys.FirstRunServerText];
    public static string FirstRunSignInTitle => LocalizedStrings.Current[UiStringKeys.FirstRunSignInTitle];
    public static string FirstRunSignInText => LocalizedStrings.Current[UiStringKeys.FirstRunSignInText];
    public static string FirstRunReadyTitle => LocalizedStrings.Current[UiStringKeys.FirstRunReadyTitle];
    public static string FirstRunReadyText => LocalizedStrings.Current[UiStringKeys.FirstRunReadyText];
    public static string FirstRunNext => LocalizedStrings.Current[UiStringKeys.FirstRunNext];
    public static string FirstRunBack => LocalizedStrings.Current[UiStringKeys.FirstRunBack];
    public static string FirstRunFinish => LocalizedStrings.Current[UiStringKeys.FirstRunFinish];

    // ---- about / diagnostics ----
    public static string AboutWindowTitle => LocalizedStrings.Current[UiStringKeys.AboutWindowTitle];
    public static string AboutHeading => LocalizedStrings.Current[UiStringKeys.AboutHeading];
    public static string AboutDescription => LocalizedStrings.Current[UiStringKeys.AboutDescription];
    public static string AboutAppVersionLabel => LocalizedStrings.Current[UiStringKeys.AboutAppVersionLabel];
    public static string AboutDeviceNameLabel => LocalizedStrings.Current[UiStringKeys.AboutDeviceNameLabel];
    public static string AboutDeviceIdLabel => LocalizedStrings.Current[UiStringKeys.AboutDeviceIdLabel];
    public static string AboutOsLabel => LocalizedStrings.Current[UiStringKeys.AboutOsLabel];
    public static string AboutServerLabel => LocalizedStrings.Current[UiStringKeys.AboutServerLabel];
    public static string AboutSignedInLabel => LocalizedStrings.Current[UiStringKeys.AboutSignedInLabel];
    public static string AboutConnectionLabel => LocalizedStrings.Current[UiStringKeys.AboutConnectionLabel];
    public static string AboutLogsLabel => LocalizedStrings.Current[UiStringKeys.AboutLogsLabel];
    public static string AboutOpenLogFolder => LocalizedStrings.Current[UiStringKeys.AboutOpenLogFolder];
    public static string AboutOpenLogFolderFailedFormat => LocalizedStrings.Current[UiStringKeys.AboutOpenLogFolderFailedFormat];
    public static string AboutCopy => LocalizedStrings.Current[UiStringKeys.AboutCopy];
    public static string AboutCopied => LocalizedStrings.Current[UiStringKeys.AboutCopied];
    public static string AboutSecretsNote => LocalizedStrings.Current[UiStringKeys.AboutSecretsNote];
    public static string AboutNotAvailable => LocalizedStrings.Current[UiStringKeys.AboutNotAvailable];

    // ---- tray and main window entry points (week 6) ----
    public static string TraySettings => LocalizedStrings.Current[UiStringKeys.TraySettings];
    public static string TrayAbout => LocalizedStrings.Current[UiStringKeys.TrayAbout];
    public static string MainSettingsTooltip => LocalizedStrings.Current[UiStringKeys.MainSettingsTooltip];
    public static string MainAboutTooltip => LocalizedStrings.Current[UiStringKeys.MainAboutTooltip];

    // ---- errors ----
    public static string StartupFailedTitle => LocalizedStrings.Current[UiStringKeys.StartupFailedTitle];
    public static string StartupFailedMessageFormat => LocalizedStrings.Current[UiStringKeys.StartupFailedMessageFormat];
}
