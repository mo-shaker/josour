using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Josour.Core.Diagnostics;

/// <summary>
/// The macOS half of <see cref="IFirewallDiagnostics"/>, answering the same question <see cref="FirewallDiagnostics"/>
/// answers on Windows: <b>can a connection from the other device reach this machine's listener at all?</b>
/// <para>
/// The two systems model that differently, and the difference matters. Windows has a named inbound rule that the
/// installer writes, so the check is "does the rule exist". macOS has no rules by name: its application firewall decides
/// per binary, and one global switch — "block all incoming connections" — overrides every per-binary allowance there is.
/// So this class reads three things and reduces them to the one fact the caller needs.
/// </para>
/// <para>
/// All three reads work without privileges; only changing the firewall needs them, and this class never changes anything.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacFirewallDiagnostics : IFirewallDiagnostics
{
    /// <summary>Apple's application-firewall tool. Absolute, because this is a security check and <c>PATH</c> is not.</summary>
    public const string SocketFilterFw = "/usr/libexec/ApplicationFirewall/socketfilterfw";

    /// <summary>The firewall is off entirely: nothing it could do stands in the way.</summary>
    public const string ProfileOff = "off";

    /// <summary>On, deciding per application.</summary>
    public const string ProfileOn = "on";

    /// <summary>On and blocking everything inbound; per-application allowances do not apply.</summary>
    public const string ProfileBlockAll = "block-all";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly IProcessRunner _runner;
    private readonly string? _applicationPath;
    private readonly TimeSpan _timeout;

    /// <param name="applicationPath">
    /// The binary whose inbound permission to check — this process's own executable by default. <c>null</c> reduces the
    /// check to the global state, which is still worth knowing.
    /// </param>
    public MacFirewallDiagnostics(
        IProcessRunner? runner = null, string? applicationPath = null, TimeSpan? timeout = null)
    {
        _runner = runner ?? SystemProcessRunner.Instance;
        _applicationPath = applicationPath ?? SafeProcessPath();
        _timeout = timeout ?? DefaultTimeout;
    }

    public static MacFirewallDiagnostics System { get; } = new();

    public string? ApplicationPath => _applicationPath;

    public async Task<FirewallStatus> InspectAsync(CancellationToken ct)
    {
        try
        {
            var state = await GlobalStateAsync(ct).ConfigureAwait(false);
            if (state is null)
            {
                return FirewallStatus.Unknown;
            }

            if (state == 0)
            {
                // The firewall is off. Nothing is blocked, and asking about this binary would be asking a question
                // whose answer cannot matter.
                return new FirewallStatus(RulePresent: true, Profile: ProfileOff);
            }

            var blockAll = await BlockAllAsync(ct).ConfigureAwait(false);
            if (blockAll is true)
            {
                // This is the one that catches people out: the app may be listed as allowed and still never receive a
                // connection, because block-all is decided before the per-application list is consulted.
                return new FirewallStatus(RulePresent: false, Profile: ProfileBlockAll);
            }

            var profile = blockAll is null ? FirewallDiagnostics.UnknownProfile : ProfileOn;
            var allowed = await ApplicationAllowedAsync(ct).ConfigureAwait(false);
            return new FirewallStatus(allowed, profile);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Same contract as the Windows side: a diagnostic value is never worth a failure in the caller.
            return FirewallStatus.Unknown;
        }
    }

    /// <summary>0 = off, 1 = on, 2 = on for essential services. <c>null</c> when the tool could not be read.</summary>
    private async Task<int?> GlobalStateAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(SocketFilterFw, "--getglobalstate", _timeout, ct).ConfigureAwait(false);
        if (!result.Ran || result.ExitCode != 0)
        {
            return null;
        }

        // "Firewall is disabled. (State = 0)" — the number is read, never the sentence, so a future wording change
        // cannot silently turn "on" into "off".
        var match = StatePattern().Match(result.Output);
        return match.Success && int.TryParse(match.Groups[1].Value, out var state) ? state : null;
    }

    private async Task<bool?> BlockAllAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(SocketFilterFw, "--getblockall", _timeout, ct).ConfigureAwait(false);
        if (!result.Ran || result.ExitCode != 0)
        {
            return null;
        }

        // "Firewall has block all state set to enabled." / "... disabled."
        if (result.Output.Contains("set to enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return result.Output.Contains("set to disabled", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private async Task<bool?> ApplicationAllowedAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_applicationPath))
        {
            return null;
        }

        var result = await _runner
            .RunAsync(SocketFilterFw, $"--getappblocked \"{_applicationPath}\"", _timeout, ct)
            .ConfigureAwait(false);

        if (!result.Ran || result.ExitCode != 0)
        {
            return null;
        }

        // "Incoming connection to <path> is permitted." / "... is blocked."
        if (result.Output.Contains("is permitted", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Unrecognised wording stays unknown rather than becoming "blocked": the host page turns false into a warning
        // that tells the user to go and fix something, and sending them after nothing is its own kind of wrong.
        return result.Output.Contains("is blocked", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static string? SafeProcessPath()
    {
        try
        {
            return Environment.ProcessPath;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"State\s*=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StatePattern();
}
