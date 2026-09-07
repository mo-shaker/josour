using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.Api;

/// <summary>Why a typed server address was accepted or refused. Each value maps to one sentence in the settings UI.</summary>
public enum ServerCheckStatus
{
    /// <summary><c>GET /healthz</c> answered as a Josour server.</summary>
    Ok,

    /// <summary>Not an absolute URL, or one carrying a query/fragment, or with no host.</summary>
    InvalidUrl,

    /// <summary>Parsed, but plain <c>http</c> to something other than loopback: the app refuses to send tokens in the clear.</summary>
    InsecureScheme,

    /// <summary>Nothing answered: DNS, TCP, TLS or the timeout.</summary>
    Unreachable,

    /// <summary>Something answered, but not a Josour health endpoint (wrong host, a captive portal, a proxy error page).</summary>
    NotJosour,
}

/// <summary>The outcome of checking a server address, and the normalized URL to store when it is <see cref="ServerCheckStatus.Ok"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="NormalizedUrl">The URL to persist (no trailing slash), or null when the address was not usable.</param>
/// <param name="Detail">A short technical detail for the log and the second line of the UI; never prose for the user.</param>
public sealed record ServerCheckResult(ServerCheckStatus Status, string? NormalizedUrl, string? Detail)
{
    public bool IsOk => Status == ServerCheckStatus.Ok;
}

/// <summary>Validates a server address and asks it whether it is alive. Injectable so the settings UI can be tested.</summary>
public interface IServerCheck
{
    /// <summary>Never throws except for <paramref name="ct"/>: every failure is a <see cref="ServerCheckResult"/>.</summary>
    Task<ServerCheckResult> CheckAsync(string? url, CancellationToken ct);
}

/// <summary>
/// <see cref="IServerCheck"/> over <c>GET {base}/healthz</c> — the one unauthenticated endpoint outside <c>/api/v1</c>
/// (docs/runbook.md), so the check works before anyone has signed in, which is exactly when it is needed.
/// <para>
/// It runs on its own short budget rather than the API client's 15 s: this is a person waiting in front of a text box
/// having just typed an address, and a wrong address is the common case at that moment. Validation comes first and is
/// shared with the rest of the app (<see cref="ServerUrl"/>), so "https only, loopback excepted" cannot drift between the
/// settings window and what the API client will actually accept afterwards.
/// </para>
/// </summary>
public sealed class HttpServerCheck : IServerCheck, IDisposable
{
    /// <summary>The health endpoint of docs/runbook.md, relative to the server's base URL.</summary>
    public const string HealthPath = "healthz";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <summary>Builds its own client over <paramref name="handler"/> (or the default one).</summary>
    public HttpServerCheck(HttpMessageHandler? handler = null, TimeSpan? timeout = null, ILogger<HttpServerCheck>? logger = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _timeout;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Validation only, without touching the network: the part the sign-in window needs before it may enable its button.</summary>
    public static ServerCheckResult Validate(string? url)
    {
        if (!ServerUrl.TryNormalize(url, out var baseUri))
        {
            return new ServerCheckResult(ServerCheckStatus.InvalidUrl, null, null);
        }

        if (!ServerUrl.IsAllowed(baseUri))
        {
            return new ServerCheckResult(ServerCheckStatus.InsecureScheme, null, baseUri.Scheme);
        }

        return new ServerCheckResult(ServerCheckStatus.Ok, Normalize(baseUri), null);
    }

    /// <summary>The URL as it is persisted in settings: absolute, normalized, without the trailing slash.</summary>
    public static string Normalize(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return baseUri.ToString().TrimEnd('/');
    }

    public async Task<ServerCheckResult> CheckAsync(string? url, CancellationToken ct)
    {
        var validated = Validate(url);
        if (!validated.IsOk)
        {
            return validated;
        }

        ServerUrl.TryNormalize(url, out var baseUri);
        var health = new Uri(baseUri, HealthPath);

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_timeout);

            using var response = await _http.GetAsync(health, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Server check {Url}: HTTP {Status}", health, (int)response.StatusCode);
                return new ServerCheckResult(ServerCheckStatus.NotJosour, null, $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (!LooksHealthy(body))
            {
                _logger.LogInformation("Server check {Url}: 200 but not a Josour health response", health);
                return new ServerCheckResult(ServerCheckStatus.NotJosour, null, "unexpected body");
            }

            _logger.LogInformation("Server check {Url}: healthy", health);
            return new ServerCheckResult(ServerCheckStatus.Ok, validated.NormalizedUrl, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Server check {Url}: no answer within {Timeout:0.#} s", health, _timeout.TotalSeconds);
            return new ServerCheckResult(ServerCheckStatus.Unreachable, null, $"timeout after {_timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("Server check {Url}: {Message}", health, ex.Message);
            return new ServerCheckResult(ServerCheckStatus.Unreachable, null, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UriFormatException)
        {
            _logger.LogWarning(ex, "Server check {Url} failed", health);
            return new ServerCheckResult(ServerCheckStatus.Unreachable, null, ex.Message);
        }
    }

    /// <summary><c>{"status":"ok"}</c> — anything else answering on that path is not the server we are looking for.</summary>
    private static bool LooksHealthy(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Disposes the client this instance built; an injected handler is left alone (its owner disposes it).</summary>
    public void Dispose() => _http.Dispose();
}
