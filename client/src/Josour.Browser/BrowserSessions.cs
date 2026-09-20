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
}
