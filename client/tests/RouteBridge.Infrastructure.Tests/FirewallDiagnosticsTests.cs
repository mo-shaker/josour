using RouteBridge.Core.Diagnostics;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// The firewall check that used to exist twice — once in track B's tunnel diagnostics and once in this layer's host probe —
/// and now lives once, in <c>RouteBridge.Core</c>. netsh is driven by a scripted runner, so the parsing is tested on every
/// platform, including the case that matters most in the field: a non-English Windows.
/// </summary>
public sealed class FirewallDiagnosticsTests
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

    private static ScriptedProcessRunner Runner(string ruleOutput, int ruleExit, string profileOutput, int profileExit = 0) =>
        new(arguments => arguments.Contains("show rule", StringComparison.Ordinal)
            ? new ProcessRunResult(ruleExit, ruleOutput)
            : new ProcessRunResult(profileExit, profileOutput));

    private static FirewallDiagnostics Check(ScriptedProcessRunner runner, bool windows = true) =>
        new(runner, isWindows: windows);

    [Fact]
    public async Task RuleAndProfile_AreReadFromNetsh()
    {
        var runner = Runner(RuleFound, 0, CurrentProfileEnglish);

        var status = await Check(runner).InspectAsync(None);

        Assert.True(status.RulePresent);
        Assert.False(status.RuleMissing);
        Assert.Equal("public", status.Profile);
        Assert.Contains(runner.Commands, c => c.Contains(FirewallDiagnostics.RuleName, StringComparison.Ordinal) && c.Contains("dir=in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSuchRule_IsAMissingRule_NotAnUnknownOne()
    {
        // netsh exits non-zero with a localized "No rules match the specified criteria": the exit code carries the answer.
        var runner = Runner("لا توجد قواعد تطابق المعايير المحددة.", 1, CurrentProfileEnglish);

        var status = await Check(runner).InspectAsync(None);

        Assert.False(status.RulePresent);
        Assert.True(status.RuleMissing);
    }

    [Fact]
    public async Task NetshThatCannotBeRun_LeavesBothValuesUnknown()
    {
        var runner = new ScriptedProcessRunner(_ => ProcessRunResult.NotRun);

        var status = await Check(runner).InspectAsync(None);

        Assert.Null(status.RulePresent);
        Assert.Null(status.Profile);
        Assert.False(status.RuleMissing); // "we could not look" must never read as "the rule is missing"
    }

    [Fact]
    public async Task ALocalizedWindows_ReportsAnUnknownProfileRatherThanAGuess()
    {
        var runner = Runner(RuleFound, 0, CurrentProfileArabic);

        var status = await Check(runner).InspectAsync(None);

        Assert.Equal(FirewallDiagnostics.UnknownProfile, status.Profile);
        Assert.True(status.RulePresent); // the rule check does not depend on the language
    }

    [Fact]
    public async Task SeveralActiveProfiles_AreAllReported()
    {
        var runner = Runner(RuleFound, 0, "\nDomain Profile Settings:\n---\nState ON\n\nPrivate Profile Settings:\n---\nState ON\n");

        var status = await Check(runner).InspectAsync(None);

        Assert.Equal("domain,private", status.Profile);
    }

    [Fact]
    public async Task AFailingProfileCommand_IsUnknown_WhileTheRuleIsStillAnswered()
    {
        var runner = Runner(RuleFound, 0, string.Empty, profileExit: 1);

        var status = await Check(runner).InspectAsync(None);

        Assert.True(status.RulePresent);
        Assert.Equal(FirewallDiagnostics.UnknownProfile, status.Profile);
    }

    [Fact]
    public async Task OffWindows_NetshIsNeverRun()
    {
        var runner = Runner(RuleFound, 0, CurrentProfileEnglish);

        var status = await Check(runner, windows: false).InspectAsync(None);

        Assert.Empty(runner.Commands);
        Assert.Null(status.RulePresent);
    }

    [Fact]
    public async Task ARunnerThatThrows_IsNotTheCallersProblem()
    {
        var runner = new ScriptedProcessRunner(_ => throw new InvalidOperationException("netsh exploded"));

        var status = await Check(runner).InspectAsync(None);

        Assert.Equal(FirewallStatus.Unknown, status);
    }

    [Fact]
    public async Task TheSystemImplementation_AnswersOnThisMachineWithoutThrowing()
    {
        // Whatever this machine is, one shared instance must give a definite, repeatable answer.
        var first = await FirewallDiagnostics.InspectSystemAsync(None);
        var second = await FirewallDiagnostics.InspectSystemAsync(None);

        Assert.Equal(first.RulePresent, second.RulePresent);
    }
}
