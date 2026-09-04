using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RouteBridge.Browser;
using RouteBridge.Browser.Registry;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Browser;
using RouteBridge.Proxy;

namespace RouteBridge.Spike;

/// <summary>أداة مصفوفة المتصفح (Chrome/Edge × مُدار/غير مُدار × Win10/11). كل شيء في JSON على stdout.</summary>
public static class BrowserCommand
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepOpen = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Graceful = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var selfHosted = args.Has("self-hosted");
        var proxyPortArg = args.GetInt("proxy-port", 0);
        if (!selfHosted && proxyPortArg is < 1 or > 65535) throw new UsageException("--proxy-port N (1..65535) or --self-hosted is required");
        var kind = (args.Get("browser") ?? "chrome").ToLowerInvariant() switch
        {
            "chrome" => BrowserKind.Chrome,
            "edge" => BrowserKind.Edge,
            _ => throw new UsageException("--browser must be chrome or edge"),
        };
        var profile = args.Get("profile") ?? BrowserCommandLine.DefaultProfileDirectory();

        IRegistryReader registry = OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : NullRegistryReader.Instance;
        var locator = new BrowserLocator(registry);
        var location = locator.Locate(kind);
        var policy = PolicyDetector.Detect(kind, registry);
        var launcher = new BrowserLauncher(registry, locator);

        var report = new Dictionary<string, object?>
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["browser"] = kind == BrowserKind.Chrome ? "chrome" : "edge",
            ["path"] = location?.Path,
            ["path_source"] = location?.Source,
            ["publisher"] = location?.Publisher,
            ["publisher_verified"] = location?.PublisherVerified,
            ["policy"] = new Dictionary<string, object?>
            {
                ["proxy_managed"] = policy.ProxyManaged,
                ["user_data_dir_managed"] = policy.UserDataDirManaged,
                ["findings"] = policy.Findings.ToList(),
            },
            ["profile"] = profile,
            ["self_hosted"] = selfHosted,
        };
        Console.Error.WriteLine($"[browser] {report["browser"]}: path={location?.Path ?? "(not found)"} publisher_verified={location?.PublisherVerified?.ToString() ?? "n/a"} proxy_managed={policy.ProxyManaged} user_data_dir_managed={policy.UserDataDirManaged}");

        ConnectProxyServer? proxy = null;
        var probeHit = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxyPort = proxyPortArg;
        if (selfHosted)
        {
            proxy = new ConnectProxyServer(new ConnectProxyOptions
            {
                Allowlist = AllowlistMatcher.Parse(0, Array.Empty<string>()), // كل شيء مباشر
                PeerPublicIp = "(self-hosted: your own IP)",
                Browser = launcher,
                OwnerPidChecker = OperatingSystem.IsWindows() ? WindowsOwnerPidChecker.Instance : PermissiveOwnerPidChecker.Instance,
                RejectUnknownOwner = false, // الأداة تقيس ولا تمنع؛ العدّادات تُبلّغ
            });
            proxy.ProbeHit += t => probeHit.TrySetResult(t);
            proxy.Start();
            proxyPort = proxy.Port;
            Console.Error.WriteLine($"[browser] self-hosted proxy on 127.0.0.1:{proxyPort} (empty allowlist, everything direct)");
        }
        report["proxy_port"] = proxyPort;

        var options = new BrowserLaunchOptions(kind, proxyPort, profile, "http://check.routebridge/");
        report["command_line"] = BrowserCommandLine.Render(location?.Path ?? BrowserLocator.ExeName(kind), BrowserCommandLine.Arguments(options));

        var exit = 2;
        try
        {
            var launchClock = Stopwatch.StartNew();
            var result = await launcher.LaunchAsync(options, ct);
            report["launch"] = new Dictionary<string, object?>
            {
                ["success"] = result.Success,
                ["failure"] = result.Failure?.ToString(),
                ["detail"] = result.Detail,
                ["launch_ms"] = launchClock.ElapsedMilliseconds,
                ["pid"] = launcher.ProcessId,
                ["handoff"] = launcher.InstanceHandoff,
                ["exit_type_set"] = launcher.ExitTypeSet,
            };
            Console.Error.WriteLine($"[browser] launch: success={result.Success} failure={result.Failure} handoff={launcher.InstanceHandoff} {result.Detail}");

            if (result.Success)
            {
                if (proxy is not null)
                {
                    var probeClock = Stopwatch.StartNew();
                    try
                    {
                        await probeHit.Task.WaitAsync(ProbeTimeout, ct);
                        report["probe_hit_ms"] = probeClock.ElapsedMilliseconds;
                        report["probe_timeout"] = false;
                        Console.Error.WriteLine($"[browser] probe page hit after {probeClock.ElapsedMilliseconds} ms");
                        exit = 0;
                    }
                    catch (TimeoutException)
                    {
                        report["probe_hit_ms"] = null;
                        report["probe_timeout"] = true;
                        Console.Error.WriteLine($"[browser] probe page NOT hit within {ProbeTimeout.TotalSeconds:F0} s → browser is not using the proxy (browser_not_proxied)");
                    }
                }
                else
                {
                    report["probe_hit_ms"] = null;
                    report["probe_timeout"] = null;
                    report["probe_note"] = "external proxy: probe hit is observed by that proxy, not by this tool";
                    exit = 0;
                }

                await Task.Delay(KeepOpen, ct);
                var closeClock = Stopwatch.StartNew();
                await launcher.CloseAsync(Graceful, ct);
                report["close_ms"] = closeClock.ElapsedMilliseconds;
                report["still_running_after_close"] = launcher.IsRunning;
                Console.Error.WriteLine($"[browser] closed in {closeClock.ElapsedMilliseconds} ms (still_running={launcher.IsRunning})");
            }
        }
        finally
        {
            if (proxy is not null)
            {
                report["proxy_counters"] = new Dictionary<string, object?>
                {
                    ["accepted"] = proxy.Counters.Accepted,
                    ["rejected_by_owner"] = proxy.Counters.RejectedByOwner,
                    ["probe_hits"] = proxy.Counters.ProbeHits,
                    ["direct_connects"] = proxy.Counters.DirectConnects,
                    ["direct_http_requests"] = proxy.Counters.DirectHttpRequests,
                    ["rejected"] = proxy.Counters.Rejected,
                    ["errors"] = proxy.Counters.Errors,
                };
                await proxy.DisposeAsync();
            }
            await launcher.DisposeAsync();
        }

        Console.WriteLine(JsonSerializer.Serialize(report, Json.Pretty));
        return exit;
    }
}
