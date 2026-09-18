using System.Runtime.Versioning;
using Josour.Core.Diagnostics;

namespace Josour.Core.Tests;

/// <summary>
/// The parsing is tested with a fake runner so it runs everywhere; one test at the end talks to the real tool on a mac.
/// The question behind all of them is the one the host page asks: can the other device's connection reach this machine?
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacFirewallDiagnosticsTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessRunResult> _answers;

        public FakeRunner(Dictionary<string, ProcessRunResult> answers) => _answers = answers;

        public List<string> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(arguments);
            var key = arguments.Split(' ')[0];
            return Task.FromResult(_answers.TryGetValue(key, out var answer) ? answer : ProcessRunResult.NotRun);
        }
    }

    private static ProcessRunResult Ok(string output) => new(0, output);

    private static MacFirewallDiagnostics Build(
        string? globalState = null, string? blockAll = null, string? appBlocked = null, string? appPath = "/Applications/Josour.app/Contents/MacOS/Josour")
    {
        var answers = new Dictionary<string, ProcessRunResult>();
        if (globalState is not null) answers["--getglobalstate"] = Ok(globalState);
        if (blockAll is not null) answers["--getblockall"] = Ok(blockAll);
        if (appBlocked is not null) answers["--getappblocked"] = Ok(appBlocked);
        return new MacFirewallDiagnostics(new FakeRunner(answers), appPath);
    }

    [Fact]
    public async Task A_firewall_that_is_off_blocks_nothing()
    {
        var status = await Build(globalState: "Firewall is disabled. (State = 0)").InspectAsync(CancellationToken.None);

        Assert.True(status.RulePresent);
        Assert.False(status.RuleMissing);
        Assert.Equal(MacFirewallDiagnostics.ProfileOff, status.Profile);
    }

    [Fact]
    public async Task Block_all_beats_a_permitted_application()
    {
        // The trap this class exists for: the app is listed as allowed and still never receives a connection.
        var status = await Build(
            globalState: "Firewall is enabled. (State = 1)",
            blockAll: "Firewall has block all state set to enabled.",
            appBlocked: "Incoming connection to /Applications/Josour.app/Contents/MacOS/Josour is permitted.")
            .InspectAsync(CancellationToken.None);

        Assert.True(status.RuleMissing);
        Assert.Equal(MacFirewallDiagnostics.ProfileBlockAll, status.Profile);
    }

    [Fact]
    public async Task An_allowed_application_behind_an_enabled_firewall_can_be_reached()
    {
        var status = await Build(
            globalState: "Firewall is enabled. (State = 1)",
            blockAll: "Firewall has block all state set to disabled.",
            appBlocked: "Incoming connection to /Applications/Josour.app/Contents/MacOS/Josour is permitted.")
            .InspectAsync(CancellationToken.None);

        Assert.True(status.RulePresent);
        Assert.Equal(MacFirewallDiagnostics.ProfileOn, status.Profile);
    }

    [Fact]
    public async Task A_blocked_application_is_reported_as_blocked()
    {
        var status = await Build(
            globalState: "Firewall is enabled. (State = 1)",
            blockAll: "Firewall has block all state set to disabled.",
            appBlocked: "Incoming connection to /Applications/Josour.app/Contents/MacOS/Josour is blocked.")
            .InspectAsync(CancellationToken.None);

        Assert.True(status.RuleMissing);
    }

    [Fact]
    public async Task The_state_is_read_as_a_number_not_as_a_sentence()
    {
        // If the wording ever changes, the number must still decide. A parser that looked for "disabled" would read
        // "Firewall is enabled" as off, because the one contains the other.
        var status = await Build(globalState: "Firewall is enabled. (State = 2)", blockAll: "Firewall has block all state set to disabled.", appBlocked: "Incoming connection to x is permitted.")
            .InspectAsync(CancellationToken.None);

        Assert.NotEqual(MacFirewallDiagnostics.ProfileOff, status.Profile);
    }

    [Fact]
    public async Task A_tool_that_cannot_be_run_is_unknown_not_a_no()
    {
        var status = await new MacFirewallDiagnostics(new FakeRunner(new()), "/x").InspectAsync(CancellationToken.None);

        Assert.Null(status.RulePresent);
        Assert.False(status.RuleMissing); // "could not tell" must never reach the user as "your firewall is blocking it"
        Assert.Null(status.Profile);
    }

    [Fact]
    public async Task Unrecognised_wording_is_unknown_rather_than_blocked()
    {
        var status = await Build(
            globalState: "Firewall is enabled. (State = 1)",
            blockAll: "Firewall has block all state set to disabled.",
            appBlocked: "something nobody has seen before")
            .InspectAsync(CancellationToken.None);

        Assert.Null(status.RulePresent);
        Assert.False(status.RuleMissing);
    }

    [Fact]
    public async Task With_no_application_path_the_global_state_is_still_reported()
    {
        var status = await Build(
            globalState: "Firewall is enabled. (State = 1)",
            blockAll: "Firewall has block all state set to disabled.",
            appPath: null)
            .InspectAsync(CancellationToken.None);

        Assert.Null(status.RulePresent);
        Assert.Equal(MacFirewallDiagnostics.ProfileOn, status.Profile);
    }

    [MacFact]
    public async Task The_real_tool_answers_on_this_machine()
    {
        // Proves the path, the arguments and the exit code, which no fake can: socketfilterfw's read operations need
        // no privileges, so this is safe to run anywhere.
        var status = await MacFirewallDiagnostics.System.InspectAsync(CancellationToken.None);

        Assert.NotNull(status.Profile);
        Assert.Contains(
            status.Profile,
            new[] { MacFirewallDiagnostics.ProfileOff, MacFirewallDiagnostics.ProfileOn, MacFirewallDiagnostics.ProfileBlockAll, FirewallDiagnostics.UnknownProfile });
    }
}
