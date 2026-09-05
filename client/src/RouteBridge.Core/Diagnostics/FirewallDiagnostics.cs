namespace RouteBridge.Core.Diagnostics;

/// <summary>
/// The Windows Firewall facts <c>hello.diagnostics</c> carries (docs/ws-protocol.md section 3): whether the inbound rule
/// the installer writes is there, and which profile is active. <c>null</c> means "could not be determined on this machine"
/// — which is not the same as "no" and must never be reported as one.
/// </summary>
/// <param name="RulePresent">An inbound rule named <see cref="FirewallDiagnostics.RuleName"/> exists.</param>
/// <param name="Profile">The active profile(s), or <see cref="FirewallDiagnostics.UnknownProfile"/> when the words could not be read.</param>
public readonly record struct FirewallStatus(bool? RulePresent, string? Profile)
{
    /// <summary>Nothing could be determined (not Windows, or netsh could not be run).</summary>
    public static FirewallStatus Unknown => new(null, null);

    /// <summary>The rule is definitely missing: incoming tunnel connections may be dropped without any visible error.</summary>
    public bool RuleMissing => RulePresent is false;
}

/// <summary>Reads <see cref="FirewallStatus"/> for this machine. Injectable so the callers can be tested off Windows.</summary>
public interface IFirewallDiagnostics
{
    /// <summary>Never throws: anything that cannot be determined comes back as <c>null</c>.</summary>
    Task<FirewallStatus> InspectAsync(CancellationToken ct);
}

/// <summary>
/// The single owner of the firewall check for the whole client (week 6). It used to exist twice — once in
/// <c>RouteBridge.Tunnel.Diagnostics.HostDiagnostics</c> (track B, for <c>hello.diagnostics</c>) and once in
/// <c>RouteBridge.Infrastructure.Diagnostics.HostDiagnosticsProbe</c> (track C, for the host's readiness warning) — with two
/// slightly different netsh invocations and two different parsers. Infrastructure cannot reference Tunnel and Tunnel has no
/// business referencing Infrastructure, so the shared, framework-free half lives here in Core, which both already reference.
///
/// <para><b>Locale.</b> netsh prints in the machine's display language, so nothing here depends on English prose:</para>
/// <list type="bullet">
///   <item>The rule check reads the EXIT CODE first (netsh exits non-zero with a localized "No rules match the specified
///     criteria") and only then looks for the rule NAME, which is identical on every Windows because the installer wrote it.</item>
///   <item>The profile is the one value that has to read a word back. On a non-English Windows it stays
///     <see cref="UnknownProfile"/> rather than guessing: the field is diagnostic, and a wrong value is worse than a missing
///     one. (A locale-independent profile needs <c>INetworkListManager</c>; noted for the spike.)</item>
/// </list>
/// </summary>
public sealed class FirewallDiagnostics : IFirewallDiagnostics
{
    /// <summary>The rule the installer adds (RouteBridge.iss): <c>netsh advfirewall firewall add rule name="RouteBridge Tunnel" …</c>.</summary>
    public const string RuleName = "RouteBridge Tunnel";

    /// <summary>What a profile that could not be determined is reported as.</summary>
    public const string UnknownProfile = "unknown";

    /// <summary>Total budget for one netsh invocation.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The profile words netsh prints on an English Windows; anything else stays <see cref="UnknownProfile"/>.</summary>
    private static readonly string[] ProfileWords = { "domain", "private", "public" };

    private readonly IProcessRunner _runner;
    private readonly bool _windows;
    private readonly TimeSpan _timeout;

    public FirewallDiagnostics(IProcessRunner? runner = null, bool? isWindows = null, TimeSpan? timeout = null)
    {
        _runner = runner ?? SystemProcessRunner.Instance;
        _windows = isWindows ?? OperatingSystem.IsWindows();
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The system implementation, for callers that have nothing to inject.</summary>
    public static FirewallDiagnostics System { get; } = new();

    /// <summary>
    /// One call for both values. This is the entry point the tunnel's <c>HostDiagnostics</c> is meant to use in place of its
    /// own netsh pair: <c>var firewall = await FirewallDiagnostics.InspectSystemAsync(ct);</c> then
    /// <c>firewall.RulePresent</c> / <c>firewall.Profile</c>.
    /// </summary>
    public static Task<FirewallStatus> InspectSystemAsync(CancellationToken ct) => System.InspectAsync(ct);

    public async Task<FirewallStatus> InspectAsync(CancellationToken ct)
    {
        if (!_windows)
        {
            // netsh is Windows-only; on a developer machine both values are simply unknown.
            return FirewallStatus.Unknown;
        }

        try
        {
            var rule = await RulePresentAsync(ct).ConfigureAwait(false);
            var profile = await ProfileAsync(ct).ConfigureAwait(false);
            return new FirewallStatus(rule, profile);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A diagnostic value is never worth a failure in the caller.
            return FirewallStatus.Unknown;
        }
    }

    private async Task<bool?> RulePresentAsync(CancellationToken ct)
    {
        var result = await _runner
            .RunAsync("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" dir=in", _timeout, ct)
            .ConfigureAwait(false);

        if (!result.Ran)
        {
            return null;
        }

        return result.ExitCode == 0 && result.Output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ProfileAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync("netsh", "advfirewall show currentprofile", _timeout, ct).ConfigureAwait(false);
        if (!result.Ran)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return UnknownProfile;
        }

        // English Windows prints e.g. "Public Profile Settings:"; several profiles can be active at once.
        var profiles = new List<string>();
        foreach (var line in result.Output.Split('\n'))
        {
            var first = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
            if (first is not null && Array.IndexOf(ProfileWords, first) >= 0 && !profiles.Contains(first))
            {
                profiles.Add(first);
            }
        }

        return profiles.Count > 0 ? string.Join(",", profiles) : UnknownProfile;
    }
}
