using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Josour.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.SystemProxy;

/// <summary>Runs one command with an argument list (network service names contain spaces). Replaced in the tests.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <param name="ExitCode">null when it could not be started or timed out.</param>
/// <param name="Output">Standard output and standard error together.</param>
public sealed record CommandResult(int? ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class SystemCommandRunner : ICommandRunner
{
    public static SystemCommandRunner Instance { get; } = new();

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) { return new CommandResult(null, ex.Message); }
        if (process is null) return new CommandResult(null, "the process did not start");
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                return new CommandResult(null, "timed out");
            }
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return new CommandResult(process.ExitCode, output);
        }
    }
}

/// <summary>
/// MACOS: every enabled network service's web proxy, secure web proxy, bypass list, PAC and auto-discovery, through
/// <c>/usr/sbin/networksetup</c> — the same fields System Settings → Network → Proxies shows, which Safari, Chrome,
/// Edge and Firefox (in "use system proxy settings") all follow.
/// <para>
/// <b>Authorisation.</b> On most Macs an administrator's account changes these silently. When <c>networksetup</c> refuses
/// (a standard account, or a macOS that wants authorisation), the same batch of commands runs once through
/// <c>osascript … with administrator privileges</c>: macOS's own password dialog, one per apply and one per restore,
/// never a stored password. A cancelled dialog is <c>authorization_denied</c>.
/// </para>
/// </summary>
public sealed class MacSystemProxyController : ISystemProxyController
{
    public const string PlatformName = "macos-networksetup";
    public const string NetworkSetup = "/usr/sbin/networksetup";
    private const string OsaScript = "/usr/bin/osascript";

    /// <summary>What never goes to the proxy: loopback, link-local and private ranges, mDNS names.</summary>
    public static readonly IReadOnlyList<string> BaseBypass = new[]
    {
        "localhost", "127.0.0.1", "::1", "*.local", "169.254/16", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
    };

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly ICommandRunner _runner;
    private readonly ILogger _logger;

    public MacSystemProxyController(ICommandRunner? runner = null, ILogger<MacSystemProxyController>? logger = null)
    {
        _runner = runner ?? SystemCommandRunner.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool IsSupported => true;

    public async Task<SystemProxySnapshot> CaptureAsync(CancellationToken ct)
    {
        var services = await EnabledServicesAsync(ct).ConfigureAwait(false);
        var saved = new List<ServiceSettings>();
        foreach (var service in services)
        {
            saved.Add(await ReadServiceAsync(service, ct).ConfigureAwait(false)
                      ?? throw new SystemProxyException("read_failed", $"the proxy settings of \"{service}\" could not be read"));
        }
        return new SystemProxySnapshot(PlatformName, JsonSerializer.Serialize(saved, Json));
    }

    public async Task ApplyAsync(SystemProxyTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var port = target.Port.ToString(CultureInfo.InvariantCulture);
        var bypass = BypassList(target.ExtraBypass);
        var commands = new List<string[]>();
        foreach (var service in await EnabledServicesAsync(ct).ConfigureAwait(false))
        {
            // The "authenticated" argument is left out on purpose, here and in the restore: passing it would need the user's
            // proxy password, which we never hold, and leaving it out leaves that setting as it was.
            commands.Add(new[] { "-setwebproxy", service, SystemProxyTarget.LoopbackHost, port });
            commands.Add(new[] { "-setwebproxystate", service, "on" });
            commands.Add(new[] { "-setsecurewebproxy", service, SystemProxyTarget.LoopbackHost, port });
            commands.Add(new[] { "-setsecurewebproxystate", service, "on" });
            commands.Add(new[] { "-setproxybypassdomains", service }.Concat(bypass).ToArray());
            commands.Add(new[] { "-setautoproxystate", service, "off" });
            commands.Add(new[] { "-setproxyautodiscovery", service, "off" });
        }
        await RunBatchAsync(commands, "apply_failed", ct).ConfigureAwait(false);
    }

    public async Task<bool> IsAppliedAsync(SystemProxyTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var services = await EnabledServicesAsync(ct).ConfigureAwait(false);
        foreach (var service in services)
        {
            var now = await ReadServiceAsync(service, ct).ConfigureAwait(false);
            if (now is null || !now.Web.IsOurs(target.Port) || !now.Secure.IsOurs(target.Port) || now.PacEnabled) return false;
        }
        return services.Count > 0;
    }

    public async Task RestoreAsync(SystemProxySnapshot snapshot, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Platform != PlatformName)
        {
            throw new SystemProxyException("restore_failed", $"a {snapshot.Platform} snapshot cannot be restored on macOS");
        }

        var saved = JsonSerializer.Deserialize<List<ServiceSettings>>(snapshot.Data, Json) ?? new List<ServiceSettings>();
        var commands = new List<string[]>();
        foreach (var before in saved)
        {
            var now = await ReadServiceAsync(before.Service, ct).ConfigureAwait(false);
            if (now is null) continue; // the service is gone; nothing of ours is left on it
            commands.AddRange(RestoreCommands(before, now, port));
        }
        if (commands.Count == 0) return;
        await RunBatchAsync(commands, "restore_failed", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The commands that put one service back, field by field, touching only what still reads as ours: the user may have
    /// changed something during the session, and that change is theirs to keep.
    /// </summary>
    public static IEnumerable<string[]> RestoreCommands(ServiceSettings before, ServiceSettings now, int port)
    {
        var service = before.Service;
        foreach (var (was, current, set, state) in new[]
                 {
                     (before.Web, now.Web, "-setwebproxy", "-setwebproxystate"),
                     (before.Secure, now.Secure, "-setsecurewebproxy", "-setsecurewebproxystate"),
                 })
        {
            if (!current.IsOurs(port)) continue;
            if (!string.IsNullOrEmpty(was.Server) && was.Port > 0)
            {
                // -setwebproxy turns the proxy on, so the state always follows it.
                yield return new[] { set, service, was.Server, was.Port.ToString(CultureInfo.InvariantCulture) };
            }
            yield return new[] { state, service, was.Enabled ? "on" : "off" };
        }

        if (IsOurBypass(now.Bypass) && !before.Bypass.SequenceEqual(now.Bypass))
        {
            yield return new[] { "-setproxybypassdomains", service }.Concat(before.Bypass.Count > 0 ? before.Bypass : new[] { "Empty" }).ToArray();
        }
        if (before.PacEnabled && !now.PacEnabled) yield return new[] { "-setautoproxystate", service, "on" };
        if (before.AutoDiscovery && !now.AutoDiscovery) yield return new[] { "-setproxyautodiscovery", service, "on" };
    }

    /// <summary>The bypass list a session applied, recognised by its fixed head (<see cref="BaseBypass"/>): only then is it ours to replace.</summary>
    public static bool IsOurBypass(IReadOnlyList<string> current)
        => current.Count >= BaseBypass.Count && current.Take(BaseBypass.Count).SequenceEqual(BaseBypass, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> BypassList(IEnumerable<string> extra)
        => BaseBypass.Concat(extra.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // ---------- reading ----------

    private async Task<IReadOnlyList<string>> EnabledServicesAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(NetworkSetup, new[] { "-listallnetworkservices" }, CommandTimeout, ct).ConfigureAwait(false);
        if (!result.Succeeded || result.Output.Contains("** Error", StringComparison.Ordinal))
        {
            throw new SystemProxyException("read_failed", "networksetup could not list the network services: " + result.Output.Trim());
        }
        return ParseServices(result.Output);
    }

    /// <summary>The enabled services: the first line is a notice, a leading asterisk marks a disabled one.</summary>
    public static IReadOnlyList<string> ParseServices(string output)
        => output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith("An asterisk", StringComparison.Ordinal) && !l.StartsWith('*'))
            .ToArray();

    private async Task<ServiceSettings?> ReadServiceAsync(string service, CancellationToken ct)
    {
        async Task<string?> Get(string verb)
        {
            var r = await _runner.RunAsync(NetworkSetup, new[] { verb, service }, CommandTimeout, ct).ConfigureAwait(false);
            return r.Succeeded && !r.Output.Contains("** Error", StringComparison.Ordinal) ? r.Output : null;
        }

        var web = await Get("-getwebproxy").ConfigureAwait(false);
        var secure = await Get("-getsecurewebproxy").ConfigureAwait(false);
        var bypass = await Get("-getproxybypassdomains").ConfigureAwait(false);
        var pac = await Get("-getautoproxyurl").ConfigureAwait(false);
        var wpad = await Get("-getproxyautodiscovery").ConfigureAwait(false);
        if (web is null || secure is null || bypass is null || pac is null || wpad is null) return null;
        return new ServiceSettings(
            service,
            ParseProxy(web),
            ParseProxy(secure),
            ParseBypass(bypass),
            IsYes(Value(pac, "Enabled")),
            IsYes(Value(wpad, "Auto Proxy Discovery")));
    }

    public static ProxyField ParseProxy(string output)
    {
        int.TryParse(Value(output, "Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port);
        return new ProxyField(
            IsYes(Value(output, "Enabled")),
            Value(output, "Server") ?? string.Empty,
            port,
            IsYes(Value(output, "Authenticated Proxy Enabled")));
    }

    public static IReadOnlyList<string> ParseBypass(string output)
    {
        if (output.StartsWith("There aren't any", StringComparison.Ordinal)) return Array.Empty<string>();
        return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    private static string? Value(string output, string key)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(key + ":", StringComparison.Ordinal)) return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    private static bool IsYes(string? value) => value is "Yes" or "On" or "yes" or "on" or "1";

    // ---------- changing ----------

    /// <summary>
    /// Runs the commands one by one. The first one that macOS refuses for lack of authorisation sends the whole batch —
    /// what already ran included, every command is idempotent — through one administrator prompt instead.
    /// </summary>
    private async Task RunBatchAsync(IReadOnlyList<string[]> commands, string failure, CancellationToken ct)
    {
        foreach (var args in commands)
        {
            var r = await _runner.RunAsync(NetworkSetup, args, CommandTimeout, ct).ConfigureAwait(false);
            if (r.Succeeded && !LooksFailed(r.Output)) continue;
            if (NeedsAuthorization(r.Output))
            {
                _logger.LogInformation("networksetup needs authorisation ({Output}); asking for an administrator's password", r.Output.Trim());
                await RunAsAdministratorAsync(commands, failure, ct).ConfigureAwait(false);
                return;
            }
            throw new SystemProxyException(failure, $"networksetup {args[0]} failed: {r.Output.Trim()}");
        }
    }

    private async Task RunAsAdministratorAsync(IReadOnlyList<string[]> commands, string failure, CancellationToken ct)
    {
        var script = string.Join(" && ", commands.Select(c => ShellQuote(NetworkSetup) + " " + string.Join(' ', c.Select(ShellQuote))));
        var apple = "do shell script \"" + AppleScriptEscape(script) + "\" with administrator privileges";
        var r = await _runner.RunAsync(OsaScript, new[] { "-e", apple }, PromptTimeout, ct).ConfigureAwait(false);
        if (r.Succeeded) return;
        if (r.Output.Contains("-128", StringComparison.Ordinal) || r.Output.Contains("canceled", StringComparison.OrdinalIgnoreCase))
        {
            throw new SystemProxyException("authorization_denied", "the administrator password prompt was cancelled");
        }
        throw new SystemProxyException(failure, "networksetup failed with administrator privileges: " + r.Output.Trim());
    }

    private static bool LooksFailed(string output)
        => output.Contains("** Error", StringComparison.Ordinal) || NeedsAuthorization(output);

    public static bool NeedsAuthorization(string output)
        => output.Contains("requires admin", StringComparison.OrdinalIgnoreCase)
           || output.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
           || output.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || output.Contains("privilege", StringComparison.OrdinalIgnoreCase);

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string AppleScriptEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '"') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------- the snapshot ----------

    public sealed record ProxyField(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("authenticated")] bool Authenticated)
    {
        public bool IsOurs(int port) => Enabled && Server == SystemProxyTarget.LoopbackHost && Port == port;
    }

    public sealed record ServiceSettings(
        [property: JsonPropertyName("service")] string Service,
        [property: JsonPropertyName("web")] ProxyField Web,
        [property: JsonPropertyName("secure")] ProxyField Secure,
        [property: JsonPropertyName("bypass")] IReadOnlyList<string> Bypass,
        [property: JsonPropertyName("pac_enabled")] bool PacEnabled,
        [property: JsonPropertyName("auto_discovery")] bool AutoDiscovery);
}
