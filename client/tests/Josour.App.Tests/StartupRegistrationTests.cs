using System.Xml.Linq;
using Josour.App;
using Josour.App.Services;

namespace Josour.App.Tests;

/// <summary>
/// The LaunchAgent document. Pure and buildable on any platform, which is the point: a plist that launchd silently
/// refuses is indistinguishable, to the user, from a setting that simply does not work.
/// </summary>
public class StartupRegistrationTests
{
    private static XElement Plist(string exePath) =>
        XDocument.Parse(StartupRegistration.BuildLaunchAgent(exePath)).Root!.Element("dict")!;

    private static string[] ProgramArguments(string exePath)
    {
        var dict = Plist(exePath);
        var keys = dict.Elements().ToList();
        var index = keys.FindIndex(e => e.Name == "key" && e.Value == "ProgramArguments");
        return keys[index + 1].Elements("string").Select(e => e.Value).ToArray();
    }

    [Fact]
    public void The_agent_is_well_formed_xml_and_labelled()
    {
        var dict = Plist("/Applications/Josour.app/Contents/MacOS/Josour");
        var keys = dict.Elements("key").Select(e => e.Value).ToList();

        Assert.Contains("Label", keys);
        Assert.Contains("ProgramArguments", keys);
        Assert.Contains("RunAtLoad", keys);
        Assert.Equal(StartupRegistration.LaunchAgentLabel, dict.Elements("string").First().Value);
    }

    [Fact]
    public void The_program_and_its_switch_are_separate_arguments()
    {
        // launchd executes the program directly rather than through a shell, so one quoted command line would be
        // read as a single executable name that does not exist.
        var args = ProgramArguments("/Applications/Josour/Josour");

        Assert.Equal(new[] { "/Applications/Josour/Josour", StartupOptions.MinimizedSwitch }, args);
    }

    [Fact]
    public void A_path_with_a_space_survives()
    {
        // "/Applications/Josour App/…" is an ordinary macOS path, and it is exactly what a shell-quoted single
        // argument would break on.
        var args = ProgramArguments("/Applications/Josour App/Contents/MacOS/Josour");

        Assert.Equal("/Applications/Josour App/Contents/MacOS/Josour", args[0]);
    }

    [Fact]
    public void A_path_with_xml_significant_characters_is_escaped()
    {
        // A user's folder can contain an ampersand; unescaped it makes the whole plist unparsable, and launchd's
        // only feedback is that nothing happens at the next login.
        var args = ProgramArguments("/Users/a&b/<x>/Josour");

        Assert.Equal("/Users/a&b/<x>/Josour", args[0]);
        Assert.Contains("&amp;", StartupRegistration.BuildLaunchAgent("/Users/a&b/Josour"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_lives_where_launchd_looks_for_it()
    {
        Assert.EndsWith(
            Path.Combine("Library", "LaunchAgents", StartupRegistration.LaunchAgentLabel + ".plist"),
            StartupRegistration.LaunchAgentPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_command_line_quotes_the_executable()
    {
        Assert.Equal(
            $"\"C:\\Program Files\\Josour\\Josour.exe\" {StartupOptions.MinimizedSwitch}",
            StartupRegistration.BuildCommandLine("C:\\Program Files\\Josour\\Josour.exe"));
    }
}
