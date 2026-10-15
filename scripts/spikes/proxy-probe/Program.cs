// Spike S1/S2 for ADR-0015: does a running .NET process notice when the system proxy changes under it?
//
// The answer decides how much LoopSafeWebProxy has to do: if HttpClient.DefaultProxy follows the change, every
// client the app owns would send its own traffic (API, control channel, relay) into its own proxy unless wrapped.
//
// Start it BEFORE the spike script applies the proxy and leave it running until after the restore:
//   dotnet run --project scripts/spikes/proxy-probe              (watches for 15 minutes)
//   dotnet run --project scripts/spikes/proxy-probe -- 300       (or for 300 seconds)
//
// Once a second it asks HttpClient.DefaultProxy which proxy it would use for http://check.josour/, and sends a GET
// there through two clients: one created at start-up and kept (like the app's ApiClient), and one created fresh each
// time. check.josour does not exist, so the GET only succeeds through the spike's dummy listener, which marks its
// replies with X-Josour-Spike. A line is printed only when something changes.
using System.Net.Sockets;
using System.Runtime.InteropServices;

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) && s > 0 ? s : 900;
var target = new Uri("http://check.josour/ping");

Console.WriteLine($"{Stamp()} proxy-probe: .NET {Environment.Version}, {RuntimeInformation.OSDescription}, watching {seconds} s");
foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!string.IsNullOrEmpty(value))
        Console.WriteLine($"{Stamp()} note: {name} is set ({value}); .NET uses it instead of the system proxy, so unset it and rerun");
}

using var keptClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
string? previous = null;
var until = DateTime.UtcNow.AddSeconds(seconds);
while (DateTime.UtcNow < until)
{
    var proxy = HttpClient.DefaultProxy.GetProxy(target);
    var proxyText = proxy is null || proxy == target ? "direct" : proxy.ToString();
    var kept = await Probe(keptClient, "dotnet-kept");
    string fresh;
    using (var freshClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        fresh = await Probe(freshClient, "dotnet-fresh");
    var line = $"DefaultProxy says {proxyText}; kept client {kept}; fresh client {fresh}";
    if (line != previous)
    {
        Console.WriteLine($"{Stamp()} {line}");
        previous = line;
    }
    await Task.Delay(1000);
}
Console.WriteLine($"{Stamp()} proxy-probe: done");

static async Task<string> Probe(HttpClient client, string label)
{
    try
    {
        using var response = await client.GetAsync($"http://check.josour/ping?b={label}");
        if (response.Headers.Contains("X-Josour-Spike"))
            return "went through the proxy";
        return $"went direct (something answered for check.josour: HTTP {(int)response.StatusCode})";
    }
    catch (HttpRequestException e) when (e.InnerException is SocketException se)
    {
        return se.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "went direct (name not found)",
            SocketError.ConnectionRefused => "tried a proxy that is no longer there (refused)",
            _ => $"failed ({se.SocketErrorCode})",
        };
    }
    catch (HttpRequestException e)
    {
        return $"failed ({e.Message})";
    }
    catch (TaskCanceledException)
    {
        return "timed out";
    }
}

static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");
