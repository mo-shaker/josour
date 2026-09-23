using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser.Tests;

/// <summary>
/// The regression these cover: the work browser's availability gate used to ask <see cref="BrowserLocator"/> alone.
/// That type knows chrome.exe, the registry and the Windows default paths, so on macOS it answered "not installed"
/// however many browsers were there — and a guest session, having connected, ended with browser_not_proxied in the
/// same instant it went active.
/// </summary>
public class BrowserSessionsTests
{
    /// <summary>A locator that can find nothing: no registry, no file on disk, no environment.</summary>
    private static BrowserLocator Blind() =>
        new(NullRegistryReader.Instance, _ => false, _ => null, _ => null);

    [MacFact]
    public void On_macOS_installation_is_read_from_the_bundle_not_the_Windows_locator()
    {
        foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge })
        {
            var bundleExists = MacBrowserLocator.Locate(MacBrowserLocator.ApplicationName(kind)) is not null;

            // Blind() is what the old gate effectively was on macOS. The answer must come from the bundle instead.
            Assert.Equal(bundleExists, BrowserSessions.IsInstalled(kind, Blind()));
        }
    }

    [MacFact]
    public void On_macOS_at_least_one_browser_is_found_when_one_is_installed()
    {
        if (!Directory.Exists("/Applications/Google Chrome.app"))
        {
            return; // Nothing to assert on a machine without it; the test above still pins the mechanism.
        }

        Assert.True(BrowserSessions.IsInstalled(BrowserKind.Chrome, Blind()));
    }

    [MacFact]
    public void On_macOS_the_executable_path_is_the_one_inside_the_bundle()
    {
        var path = BrowserSessions.ExecutablePath(BrowserKind.Chrome, Blind());
        if (path is null)
        {
            return; // Chrome is not installed on this machine.
        }

        Assert.EndsWith(".app/Contents/MacOS/Google Chrome", path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// What the Spike tool got wrong: it named <see cref="BrowserLauncher"/> directly instead of asking for this
    /// platform's session, so on macOS the tool that verifies the stack could not launch a browser at all.
    /// </summary>
    [MacFact]
    public void On_macOS_the_platform_session_is_the_mac_one()
    {
        Assert.IsType<MacBrowserSession>(BrowserSessions.ForCurrentPlatform());
    }

    [Fact]
    public void Off_macOS_the_Windows_locator_still_decides()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.False(BrowserSessions.IsInstalled(BrowserKind.Chrome, Blind()));
    }
}
