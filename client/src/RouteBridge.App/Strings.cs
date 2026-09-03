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
    public const string TrayExit = "Exit";

    // ---- main window ----
    public const string HostTab = "Host";
    public const string GuestTab = "Guest";
    public const string StatusOffline = "Offline — not signed in";
    public const string StatusConnecting = "Connecting…";
    public const string StatusConnected = "Connected";
    public const string StatusReconnecting = "Reconnecting…";

    // ---- host page ----
    public const string HostPageTitle = "Share your connection";
    public const string HostPageDescription = "When you are available, colleagues can ask to browse the company's allowed sites through your internet connection.";
    public const string HostAvailableToggle = "Available for requests";
    public const string HostStatusAvailable = "Available — waiting for requests";
    public const string HostStatusNotAvailable = "Not available";
    public const string HostSignInHintTitle = "Sign-in coming soon";
    public const string HostSignInHint = "Requests only arrive after you sign in. Sign-in ships in the next build.";

    // ---- guest page ----
    public const string GuestPageTitle = "Browse through a host";
    public const string GuestPageDescription = "Pick an available host to open a work browser that routes allowed sites through their connection.";
    public const string GuestRefresh = "Refresh";
    public const string GuestNoHostsTitle = "No hosts available";
    public const string GuestNoHostsText = "Hosts appear here when a colleague turns on \"Available for requests\".";
    public const string HostReachable = "Reachable";
    public const string HostUnreachable = "Not reachable";
    public const string HostReachabilityUnknown = "Checking…";

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

    // ---- errors ----
    public const string StartupFailedTitle = "RouteBridge could not start";
    public const string StartupFailedMessageFormat = "RouteBridge could not start.\n\n{0}\n\nSee the log folder %LOCALAPPDATA%\\RouteBridge\\logs for details.";
}
