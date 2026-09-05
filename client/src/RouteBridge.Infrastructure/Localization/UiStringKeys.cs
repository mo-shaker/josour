namespace RouteBridge.Infrastructure.Localization;

/// <summary>
/// The name of every user-facing string, exactly once. Both resource sets (<c>StringsAr.resx</c> and <c>StringsEn.resx</c>) are
/// keyed by these names, the App's <c>Strings</c> facade only ever asks for one of them, and
/// <c>LocalizationTests</c> proves that the three lists agree — a key added to one language alone fails the build's tests
/// rather than showing up as a missing string in front of a user.
/// </summary>
public static class UiStringKeys
{
    // ---- product ----
    public const string AppName = "AppName";
    public const string VersionFormat = "VersionFormat";

    // ---- tray ----
    public const string TrayTooltip = "TrayTooltip";
    public const string TrayShowWindow = "TrayShowWindow";
    public const string TrayAvailableForRequests = "TrayAvailableForRequests";
    public const string TrayStartWithWindows = "TrayStartWithWindows";
    public const string TrayDebug = "TrayDebug";
    public const string TraySimulateIncomingRequest = "TraySimulateIncomingRequest";
    public const string TraySignOut = "TraySignOut";
    public const string TrayExit = "TrayExit";
    public const string TrayTooltipSignedInFormat = "TrayTooltipSignedInFormat";
    public const string TrayTooltipNotSignedIn = "TrayTooltipNotSignedIn";

    // ---- main window ----
    public const string HostTab = "HostTab";
    public const string GuestTab = "GuestTab";
    public const string StatusNotSignedIn = "StatusNotSignedIn";
    public const string StatusSignedInFormat = "StatusSignedInFormat";
    public const string StatusOffline = "StatusOffline";
    public const string StatusConnecting = "StatusConnecting";
    public const string StatusConnected = "StatusConnected";
    public const string StatusReconnecting = "StatusReconnecting";
    public const string StatusReplacedElsewhere = "StatusReplacedElsewhere";
    public const string StatusSignInExpired = "StatusSignInExpired";
    public const string StatusNoServer = "StatusNoServer";

    // ---- host page ----
    public const string HostPageTitle = "HostPageTitle";
    public const string HostPageDescription = "HostPageDescription";
    public const string HostAvailableToggle = "HostAvailableToggle";
    public const string HostStatusAvailable = "HostStatusAvailable";
    public const string HostStatusNotAvailable = "HostStatusNotAvailable";
    public const string HostStatusAnnouncing = "HostStatusAnnouncing";
    public const string HostStatusAvailabilityFailed = "HostStatusAvailabilityFailed";
    public const string HostNotConnectedTitle = "HostNotConnectedTitle";
    public const string HostNotConnectedHint = "HostNotConnectedHint";
    public const string HostFirewallWarningTitle = "HostFirewallWarningTitle";
    public const string HostFirewallWarningMessage = "HostFirewallWarningMessage";
    public const string HostSimulatedServerTitle = "HostSimulatedServerTitle";
    public const string HostSimulatedServerHint = "HostSimulatedServerHint";

    // ---- guest page ----
    public const string GuestPageTitle = "GuestPageTitle";
    public const string GuestPageDescription = "GuestPageDescription";
    public const string GuestRefresh = "GuestRefresh";
    public const string GuestNoHostsTitle = "GuestNoHostsTitle";
    public const string GuestNoHostsText = "GuestNoHostsText";
    public const string HostReachable = "HostReachable";
    public const string HostUnreachable = "HostUnreachable";
    public const string HostReachabilityUnknown = "HostReachabilityUnknown";
    public const string GuestHostsUnavailableTitle = "GuestHostsUnavailableTitle";
    public const string GuestHostsUnavailableText = "GuestHostsUnavailableText";
    public const string GuestHostsErrorFormat = "GuestHostsErrorFormat";
    public const string GuestNotSignedInText = "GuestNotSignedInText";
    public const string GuestDurationLabel = "GuestDurationLabel";
    public const string GuestRequestConnection = "GuestRequestConnection";
    public const string GuestWaitingTitle = "GuestWaitingTitle";
    public const string GuestWaitingTextFormat = "GuestWaitingTextFormat";
    public const string GuestCancelRequest = "GuestCancelRequest";
    public const string GuestRequestSentFormat = "GuestRequestSentFormat";
    public const string GuestRequestAccepted = "GuestRequestAccepted";
    public const string GuestRequestRejected = "GuestRequestRejected";
    public const string GuestRequestExpiredText = "GuestRequestExpiredText";
    public const string GuestRequestCancelled = "GuestRequestCancelled";
    public const string GuestRequestDisconnected = "GuestRequestDisconnected";
    public const string GuestErrorHostUnavailable = "GuestErrorHostUnavailable";
    public const string GuestErrorSessionExists = "GuestErrorSessionExists";
    public const string GuestErrorRequestPending = "GuestErrorRequestPending";
    public const string GuestErrorRateLimited = "GuestErrorRateLimited";
    public const string GuestErrorNotConnected = "GuestErrorNotConnected";
    public const string GuestErrorGenericFormat = "GuestErrorGenericFormat";

    // ---- session panel ----
    public const string SessionPanelTitle = "SessionPanelTitle";
    public const string SessionPeerFormat = "SessionPeerFormat";
    public const string SessionRoleHost = "SessionRoleHost";
    public const string SessionRoleGuest = "SessionRoleGuest";
    public const string SessionPhasePreparing = "SessionPhasePreparing";
    public const string SessionPhaseConnecting = "SessionPhaseConnecting";
    public const string SessionPhaseActive = "SessionPhaseActive";
    public const string SessionPhaseEnding = "SessionPhaseEnding";
    public const string SessionPhaseEnded = "SessionPhaseEnded";
    public const string SessionTimeRemainingFormat = "SessionTimeRemainingFormat";
    public const string SessionTimeUp = "SessionTimeUp";
    public const string SessionDataUsedFormat = "SessionDataUsedFormat";
    public const string SessionDisconnect = "SessionDisconnect";
    public const string SessionReopenBrowser = "SessionReopenBrowser";
    public const string SessionDismiss = "SessionDismiss";
    public const string SessionSummaryFormat = "SessionSummaryFormat";
    public const string BytesFormat = "BytesFormat";
    public const string BytesUnitB = "BytesUnitB";
    public const string BytesUnitKb = "BytesUnitKb";
    public const string BytesUnitMb = "BytesUnitMb";
    public const string BytesUnitGb = "BytesUnitGb";

    // ---- session end reasons (docs/ws-protocol.md section 5) ----
    public const string SessionEndedByYou = "SessionEndedByYou";
    public const string SessionEndedByPeer = "SessionEndedByPeer";
    public const string SessionEndedExpired = "SessionEndedExpired";
    public const string SessionEndedPeerDisconnected = "SessionEndedPeerDisconnected";
    public const string SessionEndedYouDisconnected = "SessionEndedYouDisconnected";
    public const string SessionEndedConnectFailed = "SessionEndedConnectFailed";
    public const string SessionEndedAdminTerminated = "SessionEndedAdminTerminated";
    public const string SessionEndedBrowserNotProxied = "SessionEndedBrowserNotProxied";
    public const string SessionEndedProtocolError = "SessionEndedProtocolError";
    public const string SessionEndedUnknownFormat = "SessionEndedUnknownFormat";

    // ---- work browser ----
    public const string BrowserErrorNotFound = "BrowserErrorNotFound";
    public const string BrowserErrorManagedByPolicy = "BrowserErrorManagedByPolicy";
    public const string BrowserErrorInstanceHandoff = "BrowserErrorInstanceHandoff";
    public const string BrowserErrorProbeTimeout = "BrowserErrorProbeTimeout";
    public const string BrowserErrorOther = "BrowserErrorOther";

    // ---- incoming request: the pre-accept disclosure (product document section 15) ----
    public const string IncomingRequestWindowTitle = "IncomingRequestWindowTitle";
    public const string IncomingRequestHeadingFormat = "IncomingRequestHeadingFormat";
    public const string IncomingRequestRequesterLabel = "IncomingRequestRequesterLabel";
    public const string IncomingRequestDeviceLabel = "IncomingRequestDeviceLabel";
    public const string IncomingRequestDurationLabel = "IncomingRequestDurationLabel";
    public const string IncomingRequestAllowedSitesLabel = "IncomingRequestAllowedSitesLabel";
    public const string DurationMinutesFormat = "DurationMinutesFormat";
    public const string AllowedSitesNote = "AllowedSitesNote";
    public const string AllowedSitesSummaryFormat = "AllowedSitesSummaryFormat";
    public const string AllowedSitesUnavailable = "AllowedSitesUnavailable";
    public const string AllowedSitesEmpty = "AllowedSitesEmpty";
    public const string IncomingRequestWarningTitle = "IncomingRequestWarningTitle";
    public const string IncomingRequestWarning = "IncomingRequestWarning";
    public const string IncomingRequestDisconnectHint = "IncomingRequestDisconnectHint";
    public const string CountdownFormat = "CountdownFormat";
    public const string RequestExpired = "RequestExpired";
    public const string Accept = "Accept";
    public const string Reject = "Reject";

    // ---- toasts ----
    public const string ToastIncomingRequestTitle = "ToastIncomingRequestTitle";
    public const string ToastIncomingRequestBodyFormat = "ToastIncomingRequestBodyFormat";

    // ---- debug ----
    public const string DebugSampleGuestName = "DebugSampleGuestName";
    public const string DebugSampleGuestDevice = "DebugSampleGuestDevice";

    // ---- login ----
    public const string LoginWindowTitle = "LoginWindowTitle";
    public const string LoginHeading = "LoginHeading";
    public const string LoginDescription = "LoginDescription";
    public const string LoginServerUrlLabel = "LoginServerUrlLabel";
    public const string LoginServerUrlPlaceholder = "LoginServerUrlPlaceholder";
    public const string LoginEmailLabel = "LoginEmailLabel";
    public const string LoginPasswordLabel = "LoginPasswordLabel";
    public const string LoginSignIn = "LoginSignIn";
    public const string LoginSigningIn = "LoginSigningIn";
    public const string LoginErrorServerUrl = "LoginErrorServerUrl";
    public const string LoginErrorInvalidCredentials = "LoginErrorInvalidCredentials";
    public const string LoginErrorAccountLocked = "LoginErrorAccountLocked";
    public const string LoginErrorAccountDisabled = "LoginErrorAccountDisabled";
    public const string LoginErrorDeviceRevoked = "LoginErrorDeviceRevoked";
    public const string LoginErrorDeviceRejected = "LoginErrorDeviceRejected";
    public const string LoginErrorRateLimited = "LoginErrorRateLimited";
    public const string LoginErrorUnavailable = "LoginErrorUnavailable";
    public const string LoginErrorValidationFormat = "LoginErrorValidationFormat";
    public const string LoginErrorGenericFormat = "LoginErrorGenericFormat";

    // ---- signed out notices ----
    public const string SignedOutTitle = "SignedOutTitle";
    public const string SignedOutSessionExpired = "SignedOutSessionExpired";
    public const string SignedOutDeviceRevoked = "SignedOutDeviceRevoked";
    public const string SignedOutAccountDisabled = "SignedOutAccountDisabled";

    // ---- host readiness: firewall + VPN (week 6) ----
    public const string HostVpnWarningTitle = "HostVpnWarningTitle";
    public const string HostVpnWarningMessageFormat = "HostVpnWarningMessageFormat";
    public const string ReadinessTitle = "ReadinessTitle";
    public const string ReadinessChecking = "ReadinessChecking";
    public const string ReadinessRecheck = "ReadinessRecheck";
    public const string ReadinessFirewallOk = "ReadinessFirewallOk";
    public const string ReadinessFirewallMissing = "ReadinessFirewallMissing";
    public const string ReadinessFirewallUnknown = "ReadinessFirewallUnknown";
    public const string ReadinessVpnOk = "ReadinessVpnOk";
    public const string ReadinessVpnWarningFormat = "ReadinessVpnWarningFormat";

    // ---- settings ----
    public const string SettingsWindowTitle = "SettingsWindowTitle";
    public const string SettingsHeading = "SettingsHeading";
    public const string SettingsDescription = "SettingsDescription";
    public const string SettingsServerSection = "SettingsServerSection";
    public const string SettingsServerUrlLabel = "SettingsServerUrlLabel";
    public const string SettingsServerUrlHint = "SettingsServerUrlHint";
    public const string SettingsTestConnection = "SettingsTestConnection";
    public const string SettingsTesting = "SettingsTesting";
    public const string SettingsServerOk = "SettingsServerOk";
    public const string SettingsServerInvalidUrl = "SettingsServerInvalidUrl";
    public const string SettingsServerInsecure = "SettingsServerInsecure";
    public const string SettingsServerUnreachable = "SettingsServerUnreachable";
    public const string SettingsServerNotRouteBridge = "SettingsServerNotRouteBridge";
    public const string SettingsServerDetailFormat = "SettingsServerDetailFormat";
    public const string SettingsInterfaceSection = "SettingsInterfaceSection";
    public const string SettingsLanguageLabel = "SettingsLanguageLabel";
    public const string SettingsLanguageArabic = "SettingsLanguageArabic";
    public const string SettingsLanguageEnglish = "SettingsLanguageEnglish";
    public const string SettingsLanguageRestartNote = "SettingsLanguageRestartNote";
    public const string SettingsBrowserLabel = "SettingsBrowserLabel";
    public const string SettingsBrowserHint = "SettingsBrowserHint";
    public const string SettingsBrowserAuto = "SettingsBrowserAuto";
    public const string SettingsBrowserChrome = "SettingsBrowserChrome";
    public const string SettingsBrowserEdge = "SettingsBrowserEdge";
    public const string SettingsStartupSection = "SettingsStartupSection";
    public const string SettingsStartWithWindows = "SettingsStartWithWindows";
    public const string SettingsStartWithWindowsUnavailable = "SettingsStartWithWindowsUnavailable";
    public const string SettingsStartMinimized = "SettingsStartMinimized";
    public const string SettingsSave = "SettingsSave";
    public const string SettingsCancel = "SettingsCancel";
    public const string SettingsSaved = "SettingsSaved";
    public const string SettingsSaveFailedFormat = "SettingsSaveFailedFormat";

    // ---- first run ----
    public const string FirstRunWindowTitle = "FirstRunWindowTitle";
    public const string FirstRunWelcomeHeading = "FirstRunWelcomeHeading";
    public const string FirstRunWelcomeText = "FirstRunWelcomeText";
    public const string FirstRunStepFormat = "FirstRunStepFormat";
    public const string FirstRunServerTitle = "FirstRunServerTitle";
    public const string FirstRunServerText = "FirstRunServerText";
    public const string FirstRunSignInTitle = "FirstRunSignInTitle";
    public const string FirstRunSignInText = "FirstRunSignInText";
    public const string FirstRunReadyTitle = "FirstRunReadyTitle";
    public const string FirstRunReadyText = "FirstRunReadyText";
    public const string FirstRunNext = "FirstRunNext";
    public const string FirstRunBack = "FirstRunBack";
    public const string FirstRunFinish = "FirstRunFinish";

    // ---- about / diagnostics ----
    public const string AboutWindowTitle = "AboutWindowTitle";
    public const string AboutHeading = "AboutHeading";
    public const string AboutDescription = "AboutDescription";
    public const string AboutAppVersionLabel = "AboutAppVersionLabel";
    public const string AboutDeviceNameLabel = "AboutDeviceNameLabel";
    public const string AboutDeviceIdLabel = "AboutDeviceIdLabel";
    public const string AboutOsLabel = "AboutOsLabel";
    public const string AboutServerLabel = "AboutServerLabel";
    public const string AboutSignedInLabel = "AboutSignedInLabel";
    public const string AboutConnectionLabel = "AboutConnectionLabel";
    public const string AboutLogsLabel = "AboutLogsLabel";
    public const string AboutOpenLogFolder = "AboutOpenLogFolder";
    public const string AboutOpenLogFolderFailedFormat = "AboutOpenLogFolderFailedFormat";
    public const string AboutCopy = "AboutCopy";
    public const string AboutCopied = "AboutCopied";
    public const string AboutSecretsNote = "AboutSecretsNote";
    public const string AboutNotAvailable = "AboutNotAvailable";

    // ---- tray and main window entry points (week 6) ----
    public const string TraySettings = "TraySettings";
    public const string TrayAbout = "TrayAbout";
    public const string MainSettingsTooltip = "MainSettingsTooltip";
    public const string MainAboutTooltip = "MainAboutTooltip";

    // ---- errors ----
    public const string StartupFailedTitle = "StartupFailedTitle";
    public const string StartupFailedMessageFormat = "StartupFailedMessageFormat";

    /// <summary>Every key above, in declaration order (reflected once; used by the resource loader's completeness check).</summary>
    public static IReadOnlyList<string> All { get; } = typeof(UiStringKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}
