namespace RouteBridge.App;

/// <summary>
/// Every user-facing string of the app. English for now; Arabic localization (and RTL flow) is a later week,
/// so keep ALL UI text here and bind XAML with <c>{x:Static app:Strings.X}</c> so it can be swapped in one place.
/// </summary>
public static class Strings
{
    // ---- product ----
    public const string AppName = "RouteBridge";
    public const string VersionFormat = "v{0}";

    // ---- tray ----
    public const string TrayTooltip = "RouteBridge";
    public const string TrayShowWindow = "Show window";
    public const string TrayAvailableForRequests = "Available for requests";
    public const string TrayStartWithWindows = "Start with Windows";
    public const string TrayDebug = "Debug";
    public const string TraySimulateIncomingRequest = "Simulate incoming request";
    public const string TraySignOut = "Sign out";
    public const string TrayExit = "Exit";
    public const string TrayTooltipSignedInFormat = "RouteBridge — {0}";
    public const string TrayTooltipNotSignedIn = "RouteBridge — not signed in";

    // ---- main window ----
    public const string HostTab = "Host";
    public const string GuestTab = "Guest";
    public const string StatusNotSignedIn = "Not signed in";
    public const string StatusSignedInFormat = "{0} · {1}";
    public const string StatusOffline = "Offline";
    public const string StatusConnecting = "Connecting…";
    public const string StatusConnected = "Connected";
    public const string StatusReconnecting = "Reconnecting…";

    // ---- host page ----
    public const string HostPageTitle = "Share your connection";
    public const string HostPageDescription = "When you are available, colleagues can ask to browse the company's allowed sites through your internet connection.";
    public const string HostAvailableToggle = "Available for requests";
    public const string HostStatusAvailable = "Available — waiting for requests";
    public const string HostStatusNotAvailable = "Not available";
    public const string HostNotConnectedTitle = "Not connected";
    public const string HostNotConnectedHint = "Requests only arrive while RouteBridge is connected to the server.";
    public const string HostSimulatedServerTitle = "Simulated server";
    public const string HostSimulatedServerHint = "This build talks to a built-in simulated server: hosts, requests and sessions are scripted. Real connections arrive in a later build.";

    // ---- guest page ----
    public const string GuestPageTitle = "Browse through a host";
    public const string GuestPageDescription = "Pick an available host to open a work browser that routes allowed sites through their connection.";
    public const string GuestRefresh = "Refresh";
    public const string GuestNoHostsTitle = "No hosts available";
    public const string GuestNoHostsText = "Hosts appear here when a colleague turns on \"Available for requests\".";
    public const string HostReachable = "Reachable";
    public const string HostUnreachable = "Not reachable";
    public const string HostReachabilityUnknown = "Checking…";
    public const string GuestHostsUnavailableTitle = "Could not load hosts";
    public const string GuestHostsUnavailableText = "The server cannot be reached. Check your connection and try again.";
    public const string GuestHostsErrorFormat = "The server answered: {0}";
    public const string GuestNotSignedInText = "Sign in to see available hosts.";

    // ---- incoming request ----
    public const string IncomingRequestWindowTitle = "Incoming request";
    public const string IncomingRequestHeadingFormat = "{0} wants to browse through your connection";
    public const string IncomingRequestDeviceLabel = "Device";
    public const string IncomingRequestDurationLabel = "Duration";
    public const string IncomingRequestAllowedSitesLabel = "Allowed sites";
    public const string DurationMinutesFormat = "{0} minutes";
    public const string AllowedSitesPlaceholder = "Only sites on the company allow-list can be reached through you. The current list will be shown here.";
    public const string IncomingRequestWarningTitle = "Privacy notice";
    public const string IncomingRequestWarning = "Sites will see your public IP address.";
    public const string IncomingRequestDisconnectHint = "You can disconnect the session at any time.";
    public const string CountdownFormat = "Expires in {0} s";
    public const string RequestExpired = "Request expired";
    public const string Accept = "Accept";
    public const string Reject = "Reject";

    // ---- toasts ----
    public const string ToastIncomingRequestTitle = "Incoming browsing request";
    public const string ToastIncomingRequestBodyFormat = "{0} ({1}) asks to browse through your connection for {2} minutes.";

    // ---- debug ----
    public const string DebugSampleGuestName = "Sara Ahmed";
    public const string DebugSampleGuestDevice = "SARA-LAPTOP";

    // ---- login ----
    public const string LoginWindowTitle = "Sign in";
    public const string LoginHeading = "Sign in to RouteBridge";
    public const string LoginDescription = "Use the account your administrator created for you.";
    public const string LoginServerUrlLabel = "Server address";
    public const string LoginServerUrlPlaceholder = "https://routebridge.example.com";
    public const string LoginEmailLabel = "Email";
    public const string LoginPasswordLabel = "Password";
    public const string LoginSignIn = "Sign in";
    public const string LoginSigningIn = "Signing in…";
    public const string LoginErrorServerUrl = "Enter the server address as https://… (http:// is only accepted for localhost).";
    public const string LoginErrorInvalidCredentials = "Wrong email or password.";
    public const string LoginErrorAccountLocked = "Too many failed attempts. The account is locked for 15 minutes.";
    public const string LoginErrorAccountDisabled = "This account is disabled. Contact your administrator.";
    public const string LoginErrorDeviceRevoked = "An administrator removed this device. Sign in again to register it anew.";
    public const string LoginErrorDeviceRejected = "This device could not be verified. Sign in again to register it anew.";
    public const string LoginErrorRateLimited = "Too many sign-in attempts. Wait a minute and try again.";
    public const string LoginErrorUnavailable = "The server cannot be reached. Check the address and your connection.";
    public const string LoginErrorValidationFormat = "The server rejected the request: {0}";
    public const string LoginErrorGenericFormat = "Sign-in failed ({0}).";

    // ---- signed out notices ----
    public const string SignedOutTitle = "Signed out of RouteBridge";
    public const string SignedOutSessionExpired = "Your session expired. Please sign in again.";
    public const string SignedOutDeviceRevoked = "An administrator removed this device.";
    public const string SignedOutAccountDisabled = "Your account was disabled.";

    // ---- errors ----
    public const string StartupFailedTitle = "RouteBridge could not start";
    public const string StartupFailedMessageFormat = "RouteBridge could not start.\n\n{0}\n\nSee the log folder %LOCALAPPDATA%\\RouteBridge\\logs for details.";
}
