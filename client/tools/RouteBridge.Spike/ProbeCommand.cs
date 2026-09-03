using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RouteBridge.Spike;

public static class ProbeCommand
{
    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var api = args.Require("api").TrimEnd('/');
        var token = args.Require("token");
        var ip = args.Require("ip");
        var port = args.RequireInt("port");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = $"{api}/api/v1/probe";
        Console.Error.WriteLine($"POST {url} {{ip={ip}, port={port}}}");
        try
        {
            using var response = await http.PostAsJsonAsync(url, new { ip, port }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine($"status: {(int)response.StatusCode} {response.ReasonPhrase}");
            Console.WriteLine(body);
            return response.IsSuccessStatusCode ? 0 : 2;
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"request failed: {e.Message}");
            return 2;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("request timed out");
            return 2;
        }
    }
}
