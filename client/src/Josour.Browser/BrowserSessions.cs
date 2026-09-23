using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser;

/// <summary>
/// The work browser this platform has, decided once.
/// <para>
/// Windows contains the browser in a Job Object and checks the publisher's Authenticode signature; macOS has
/// neither, so it launches the executable inside the application bundle and answers ownership from the process
/// tree. Both satisfy the one thing the proxy needs of them — <see cref="IBrowserSession.OwnsProcess"/> — and the
/// choice lives here so the app and the spike tool cannot drift into launching different browsers.
/// </para>
/// </summary>
public static class BrowserSessions
{
    /// <summary>True where a work browser can be launched and owned at all.</summary>
    public static bool SupportedOnThisPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static IBrowserSession ForCurrentPlatform(IRegistryReader? registry = null, BrowserLocator? locator = null) =>
        OperatingSystem.IsMacOS()
            ? new MacBrowserSession()
            : new BrowserLauncher(registry, locator);

    /// <summary>
    /// Whether this browser is installed, asked the way this platform installs browsers: the registry and the
    /// Windows default paths, or the application bundle under /Applications and ~/Applications.
    /// <para>
    /// It lives beside <see cref="ForCurrentPlatform"/> for that class's own reason — the check that decides a work
    /// browser exists and the code that launches it must look in the same place. They did not, once: the gate asked
    /// <see cref="BrowserLocator"/> alone, which knows chrome.exe and the registry and nothing else, so on macOS it
    /// found nothing however many browsers were installed, and every guest session ended with browser_not_proxied
    /// in the same instant it went active.
    /// </para>
    /// </summary>
    public static string? ExecutablePath(BrowserKind kind, BrowserLocator? locator = null) =>
        OperatingSystem.IsMacOS()
            ? MacBrowserLocator.Locate(MacBrowserLocator.ApplicationName(kind))
            : (locator ?? new BrowserLocator()).Locate(kind)?.Path;

    /// <inheritdoc cref="ExecutablePath"/>
    public static bool IsInstalled(BrowserKind kind, BrowserLocator? locator = null) =>
        ExecutablePath(kind, locator) is not null;
}
