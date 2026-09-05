using RouteBridge.Infrastructure.Diagnostics;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// The <c>hello.diagnostics</c> values this layer answers (docs/ws-protocol.md section 3): the firewall rule and profile
/// from netsh, and <c>ipv6_global</c> from the interfaces. netsh is driven by a scripted runner, so the parsing is tested
/// on every platform — including the case that matters most in the field, a non-English Windows.
/// </summary>
public sealed class HostDiagnosticsProbeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>netsh as an English Windows prints it for a rule that exists.</summary>
    private const string RuleFound = """

        Rule Name:                            RouteBridge Tunnel
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Domain,Private,Public
        Grouping:
        LocalIP:                              Any
        RemoteIP:                             Any
        Protocol:                             TCP
        Action:                               Allow
        """;

    private const string CurrentProfileEnglish = """

        Public Profile Settings:
        ----------------------------------------------------------------------
        State                                 ON
        Firewall Policy                       BlockInbound,AllowOutbound
        """;

    /// <summary>The same command on an Arabic Windows: nothing to match a word against.</summary>
    private const string CurrentProfileArabic = """

        إعدادات ملف تعريف عام:
        ----------------------------------------------------------------------
        الحالة                                 تشغيل
        """;

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Func<string, (int?, string)> _answer;

        public ScriptedRunner(Func<string, (int?, string)> answer) => _answer = answer;

        public List<string> Commands { get; } = new();

        public Task<(int? ExitCode, string Output)> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Commands.Add(fileName + " " + arguments);
            return Task.FromResult(_answer(arguments));
        }
    }

    private static HostDiagnosticsProbe Probe(ScriptedRunner runner, bool windows = true) => new(runner, logger: null, isWindows: windows);

    [Fact]
    public async Task RuleAndProfile_AreReadFromNetsh()
    {
        var runner = new ScriptedRunner(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? (0, RuleFound)
            : (0, CurrentProfileEnglish));

        var collected = await Probe(runner).ProbeAsync(None);

        Assert.True(collected.FirewallRulePresent);
        Assert.Equal("public", collected.FirewallProfile);
        Assert.Contains(runner.Commands, c => c.Contains("RouteBridge Tunnel", StringComparison.Ordinal) && c.Contains("dir=in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSuchRule_IsAMissingRule_NotAnUnknownOne()
    {
        // netsh exits non-zero with a localized "No rules match the specified criteria": the exit code carries the answer.
        var runner = new ScriptedRunner(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? (1, "لا توجد قواعد تطابق المعايير المحددة.")
            : (0, CurrentProfileEnglish));

        var collected = await Probe(runner).ProbeAsync(None);

        Assert.False(collected.FirewallRulePresent);
    }

    [Fact]
    public async Task NetshThatCannotBeRun_LeavesTheFirewallValuesUnknown()
    {
        var runner = new ScriptedRunner(_ => (null, string.Empty));

        var collected = await Probe(runner).ProbeAsync(None);
        var diagnostics = await Probe(runner).CollectAsync(None);

        Assert.Null(collected.FirewallRulePresent);
        Assert.Null(collected.FirewallProfile);

        // "we could not look" is left out of the frame entirely rather than sent as a null the server would store.
        Assert.False(diagnostics.ContainsKey("firewall_rule_present"));
        Assert.False(diagnostics.ContainsKey("firewall_profile"));
    }

    [Fact]
    public async Task ALocalizedWindows_ReportsAnUnknownProfileRatherThanAGuess()
    {
        var runner = new ScriptedRunner(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? (0, RuleFound)
            : (0, CurrentProfileArabic));

        var collected = await Probe(runner).ProbeAsync(None);

        Assert.Equal(HostDiagnosticsProbe.UnknownProfile, collected.FirewallProfile);
        Assert.True(collected.FirewallRulePresent); // the rule check does not depend on the language
    }

    [Fact]
    public async Task SeveralActiveProfiles_AreAllReported()
    {
        var runner = new ScriptedRunner(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? (0, RuleFound)
            : (0, "\nDomain Profile Settings:\n---\nState ON\n\nPrivate Profile Settings:\n---\nState ON\n"));

        var collected = await Probe(runner).ProbeAsync(None);

        Assert.Equal("domain,private", collected.FirewallProfile);
    }

    [Fact]
    public async Task OffWindows_NetshIsNeverRun()
    {
        var runner = new ScriptedRunner(_ => (0, RuleFound));

        var collected = await Probe(runner, windows: false).ProbeAsync(None);

        Assert.Empty(runner.Commands);
        Assert.Null(collected.FirewallRulePresent);
    }

    [Fact]
    public async Task TheWireDictionary_CarriesTheContractsKeys_AndLeavesVpnAdapterToTrackB()
    {
        var runner = new ScriptedRunner(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? (0, RuleFound)
            : (0, CurrentProfileEnglish));

        var diagnostics = await Probe(runner).CollectAsync(None);

        Assert.True((bool)diagnostics["firewall_rule_present"]!);
        Assert.Equal("public", diagnostics["firewall_profile"]);
        Assert.IsType<bool>(diagnostics["ipv6_global"]);
        Assert.False(diagnostics.ContainsKey("vpn_adapter")); // Track B's typed detector fills this in, not a second heuristic
    }

    [Fact]
    public void Ipv6Global_IsAnswerableOnThisMachine_AndNeverThrows()
    {
        // Whatever this machine has, the probe must return a definite answer without touching the network.
        var probe = new HostDiagnosticsProbe();

        var first = probe.HasGlobalIpv6();
        var second = probe.HasGlobalIpv6();

        Assert.Equal(first, second);
    }
}
