using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteBridge.Infrastructure.Diagnostics;

/// <summary>Runs a console tool and returns what it printed; abstracted so the netsh parsing can be tested without Windows.</summary>
public interface IProcessRunner
{
    /// <summary>Exit code and standard output; <c>null</c> exit code when the tool could not be run or timed out.</summary>
    Task<(int? ExitCode, string Output)> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The part of <c>hello.diagnostics</c> (docs/ws-protocol.md section 3) this layer can answer on its own:
/// <c>firewall_rule_present</c>, <c>firewall_profile</c> and <c>ipv6_global</c>.
/// <para>
/// <b>Locale.</b> netsh prints in the machine's display language, so nothing here depends on English prose. The rule check
/// reads the exit code (netsh exits non-zero when no rule matches) and only then looks for the rule NAME, which is the
/// same on every Windows because the installer wrote it. The profile is the one value that has to read a word back:
/// on a non-English Windows it stays <c>unknown</c> rather than guessing — the field is diagnostic, and a wrong value is
/// worse than a missing one. (A locale-independent profile needs <c>INetworkListManager</c>; noted for the spike.)
/// </para>
/// <para>
/// <c>vpn_adapter</c> is deliberately NOT here: Track B owns adapter detection and is replacing its heuristic with a
/// typed result (<c>RouteBridge.Core.Net.VpnDetector</c>, landing this week); a second implementation in this layer
/// would be one more thing to keep in agreement and would disagree first. <see cref="CollectAsync"/> leaves the key
/// out entirely — the contract has every diagnostics field optional — and the seam is
/// <see cref="Collected.VpnAdapter"/>, which that detector fills in next week.
/// </para>
/// </summary>
public sealed class HostDiagnosticsProbe
{
    /// <summary>The rule the installer adds (RouteBridge.iss): <c>netsh advfirewall firewall add rule name="RouteBridge Tunnel" …</c>.</summary>
    public const string FirewallRuleName = "RouteBridge Tunnel";

    /// <summary>What a profile that could not be determined is reported as.</summary>
    public const string UnknownProfile = "unknown";

    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The profile words netsh prints on an English Windows; anything else stays <see cref="UnknownProfile"/>.</summary>
    private static readonly string[] ProfileWords = { "domain", "private", "public" };

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly bool _windows;

    public HostDiagnosticsProbe(IProcessRunner? runner = null, ILogger<HostDiagnosticsProbe>? logger = null, bool? isWindows = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _windows = isWindows ?? OperatingSystem.IsWindows();
    }

    /// <summary>What this probe found; a null value means "could not be determined on this machine".</summary>
    /// <param name="FirewallRulePresent">An enabled inbound rule named <see cref="FirewallRuleName"/> exists.</param>
    /// <param name="FirewallProfile">The active firewall profile, or <see cref="UnknownProfile"/>.</param>
    /// <param name="Ipv6Global">This machine has a global (routable) IPv6 address.</param>
    /// <param name="VpnAdapter">Always null here: Track B's typed detector fills it in (see the class remarks).</param>
    public sealed record Collected(bool? FirewallRulePresent, string? FirewallProfile, bool Ipv6Global, bool? VpnAdapter = null);

    /// <summary>
    /// The diagnostics as the <c>hello</c> frame carries them. Keys are the contract's, values are JSON-friendly, and a
    /// value that could not be determined is left OUT rather than sent as null — the server stores whatever arrives, and
    /// "we did not look" should not read as "we looked and found nothing". Never throws.
    /// </summary>
    public async Task<Dictionary<string, object?>> CollectAsync(CancellationToken ct)
    {
        var collected = await ProbeAsync(ct).ConfigureAwait(false);
        var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ipv6_global"] = collected.Ipv6Global,
        };

        if (collected.FirewallRulePresent is bool present)
        {
            diagnostics["firewall_rule_present"] = present;
        }

        if (!string.IsNullOrEmpty(collected.FirewallProfile))
        {
            diagnostics["firewall_profile"] = collected.FirewallProfile;
        }

        // WEEK 6 SEAM: ["vpn_adapter"] comes from Track B's typed detection (RouteBridge.Core.Net.VpnDetector, landing
        // this week) — one call once it is merged. Do not add a second detector here; see the class remarks.
        return diagnostics;
    }

    /// <summary>The raw findings (what <see cref="CollectAsync"/> turns into the wire dictionary). Never throws.</summary>
    public async Task<Collected> ProbeAsync(CancellationToken ct)
    {
        var ipv6 = HasGlobalIpv6();
        if (!_windows)
        {
            // netsh is Windows-only; on a developer machine the two firewall values are simply unknown.
            return new Collected(FirewallRulePresent: null, FirewallProfile: null, ipv6);
        }

        var rule = await FirewallRulePresentAsync(ct).ConfigureAwait(false);
        var profile = await FirewallProfileAsync(ct).ConfigureAwait(false);
        return new Collected(rule, profile, ipv6);
    }

    /// <summary>
    /// A global unicast IPv6 address on an interface that is up — not a link-local (<c>fe80::</c>), not a unique-local
    /// (<c>fc00::/7</c>) and not the loopback: only a routable address makes the <c>v6</c> candidate type worth trying.
    /// </summary>
    public bool HasGlobalIpv6()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = address.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
                        {
                            continue;
                        }

                        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo || IPAddress.IsLoopback(ip) || IsUniqueLocal(ip))
                        {
                            continue;
                        }

                        return true;
                    }
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    // one interface refused to describe itself; the others still count
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            _logger.LogDebug("The network interfaces could not be enumerated: {Message}", ex.Message);
        }

        return false;
    }

    /// <summary><c>fc00::/7</c>: private addressing, not reachable from the other device's network.</summary>
    private static bool IsUniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

    private async Task<bool?> FirewallRulePresentAsync(CancellationToken ct)
    {
        var (exitCode, output) = await _runner
            .RunAsync("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\" dir=in", NetshTimeout, ct)
            .ConfigureAwait(false);

        if (exitCode is null)
        {
            _logger.LogDebug("netsh could not be run: firewall_rule_present stays unknown");
            return null;
        }

        // netsh exits non-zero ("No rules match the specified criteria", localized) when there is no such rule.
        var present = exitCode == 0 && output.Contains(FirewallRuleName, StringComparison.OrdinalIgnoreCase);
        if (!present)
        {
            _logger.LogInformation("No inbound firewall rule named '{RuleName}': the listener may be blocked silently", FirewallRuleName);
        }

        return present;
    }

    private async Task<string?> FirewallProfileAsync(CancellationToken ct)
    {
        var (exitCode, output) = await _runner
            .RunAsync("netsh", "advfirewall show currentprofile", NetshTimeout, ct)
            .ConfigureAwait(false);

        if (exitCode is null or not 0)
        {
            return exitCode is null ? null : UnknownProfile;
        }

        // English Windows prints e.g. "Public Profile Settings:"; several profiles can be active at once.
        var profiles = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var first = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
            if (first is not null && ProfileWords.Contains(first) && !profiles.Contains(first))
            {
                profiles.Add(first);
            }
        }

        if (profiles.Count > 0)
        {
            return string.Join(",", profiles);
        }

        _logger.LogDebug("netsh printed no recognisable profile name (localized Windows): firewall_profile stays '{Unknown}'", UnknownProfile);
        return UnknownProfile;
    }
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>: no window, output captured, killed on timeout.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<(int? ExitCode, string Output)> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return (null, string.Empty);
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
            try
            {
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // it exited on its own between the timeout and the kill
                }

                return (null, string.Empty);
            }

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return (null, string.Empty);
        }
    }
}
