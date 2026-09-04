using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RouteBridge.Spike;

/// <summary>POST /api/v1/diagnostics {session_id, role, data} (أفضل جهد؛ النقطة يضيفها المسار A في الأسبوع 2). لا يرمي أبدًا.</summary>
public static class DiagnosticsPoster
{
    public static async Task<bool> PostAsync(string apiBase, string token, Guid sessionId, string role, object data, CancellationToken ct)
    {
        var baseUrl = apiBase.TrimEnd('/');
        if (baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^"/api/v1".Length];
        var url = $"{baseUrl}/api/v1/diagnostics";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Console.Error.WriteLine($"POST {url} (session_id={sessionId}, role={role})");
        try
        {
            using var response = await http.PostAsJsonAsync(url, new { session_id = sessionId, role, data }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                case HttpStatusCode.OK:
                    Console.Error.WriteLine($"diagnostics posted: {(int)response.StatusCode} {body.Trim()}");
                    return true;
                case HttpStatusCode.NotFound:
                    Console.Error.WriteLine("diagnostics NOT posted: 404 — the backend does not expose POST /api/v1/diagnostics yet (Track A adds it in week 2); keep the JSON above and retry later");
                    return false;
                case HttpStatusCode.Unauthorized:
                    Console.Error.WriteLine("diagnostics NOT posted: 401 — access token rejected (expired? use a fresh --token)");
                    return false;
                default:
                    Console.Error.WriteLine($"diagnostics NOT posted: {(int)response.StatusCode} {response.ReasonPhrase} {body.Trim()}");
                    return false;
            }
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"diagnostics NOT posted: request failed: {e.Message}");
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("diagnostics NOT posted: request timed out");
            return false;
        }
    }
}
