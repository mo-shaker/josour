using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Control;
using Josour.Infrastructure.Settings;

namespace Josour.Infrastructure.Api;

/// <summary>
/// <see cref="IApiClient"/> over <see cref="HttpClient"/>. The base URL is read from <see cref="IAppSettingsStore"/> on every call so the
/// sign-in window can change it without restarting. Build the client with <see cref="CreateHttpClient"/> so the 15 s timeout,
/// Accept header and <see cref="AuthenticatedHandler"/> are in place.
/// </summary>
public sealed class ApiClient : IApiClient
{
    public const string BasePath = "api/v1/";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Serializer options shared by every DTO (names come from JsonPropertyName; nulls are omitted when writing).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly HttpClient _http;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger<ApiClient> _logger;

    public ApiClient(HttpClient http, IAppSettingsStore settings, ILogger<ApiClient>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger<ApiClient>.Instance;
    }

    /// <summary>
    /// The product name as it goes on the wire (User-Agent). Deliberately **not** the localized
    /// display name: an HTTP header value must be ASCII, so building it from the UI string made the
    /// Arabic build - the default one - throw at start-up while the English build was fine.
    /// This one is a protocol identifier and stays ASCII whatever the interface language is.
    /// </summary>
    public const string ProductId = "Josour";

    /// <summary>Creates the <see cref="HttpClient"/> this class expects: 15 s timeout, JSON Accept header, optional User-Agent.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler handler, string? userAgent = null, bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var client = new HttpClient(handler, disposeHandler) { Timeout = DefaultTimeout };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            // A User-Agent is diagnostic decoration. Refusing to construct the client over one the
            // header parser dislikes turns a cosmetic string into a dead application, which is
            // exactly what happened once; the request is fine without the header.
            if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            {
                client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{ProductId}/unknown");
            }
        }

        return client;
    }

    // ---------- auth (anonymous: no Bearer, no refresh on 401) ----------

    public Task<AuthResponse> LoginAsync(string email, string password, DeviceLoginInfo device, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(device);
        return PostAsync<AuthResponse>("auth/login", new LoginRequest(email.Trim(), password, device), anonymous: true, ct);
    }

    public Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return PostAsync<AuthResponse>("auth/refresh", new RefreshRequest(refreshToken), anonymous: true, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var response = await SendAsync(HttpMethod.Post, "auth/logout", new LogoutRequest(refreshToken), anonymous: true, configure: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    // ---------- current user ----------

    public Task<UserDto> GetMeAsync(CancellationToken ct) => GetAsync<UserDto>("me", ct);

    public Task<IReadOnlyList<DeviceDto>> GetMyDevicesAsync(CancellationToken ct) => GetAsync<IReadOnlyList<DeviceDto>>("me/devices", ct);

    public async Task DeleteMyDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"me/devices/{deviceId:D}", body: null, anonymous: false, configure: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<HostInfoDto>> GetHostsAsync(CancellationToken ct) => GetAsync<IReadOnlyList<HostInfoDto>>("hosts", ct);

    public Task<IReadOnlyList<SessionDto>> GetMySessionsAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return GetAsync<IReadOnlyList<SessionDto>>("sessions/me?limit=" + limit.ToString(CultureInfo.InvariantCulture), ct);
    }

    public async Task<DomainsResult> GetDomainsAsync(int? knownVersion, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "domains",
            body: null,
            anonymous: false,
            configure: request =>
            {
                if (knownVersion is int version)
                {
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(FormatETag(version)));
                }
            },
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return DomainsResult.NotModified;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var domains = await ReadJsonAsync<DomainsDto>(response, ct).ConfigureAwait(false);
        return DomainsResult.From(domains, response.Headers.ETag?.Tag);
    }

    public Task<DomainsDto> GetDomainsVersionAsync(int version, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return GetAsync<DomainsDto>("domains?version=" + version.ToString(CultureInfo.InvariantCulture), ct);
    }

    public Task<ProbeResult> ProbeAsync(string ip, int port, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return PostAsync<ProbeResult>("probe", new ProbeRequest(ip.Trim(), port), anonymous: false, ct);
    }

    public Task<DiagnosticsAccepted> PostDiagnosticsAsync(Guid? sessionId, string? role, IReadOnlyDictionary<string, object?> data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        return PostAsync<DiagnosticsAccepted>("diagnostics", new DiagnosticsRequest(sessionId, role, data), anonymous: false, ct);
    }

    /// <summary>The contract's ETag for an allow-list version: the quoted number, e.g. <c>"3"</c>.</summary>
    public static string FormatETag(int version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    // ---------- plumbing ----------

    // ---------------------------------------------------------------- admin

    public Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(string? query, CancellationToken ct)
    {
        var trimmed = query?.Trim();
        var path = string.IsNullOrEmpty(trimmed)
            ? "admin/users"
            : "admin/users?q=" + Uri.EscapeDataString(trimmed);
        return GetAsync<IReadOnlyList<AdminUserDto>>(path, ct);
    }

    public Task<AdminUserDto> CreateUserAsync(AdminUserCreate request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostAsync<AdminUserDto>("admin/users", request, anonymous: false, ct);
    }

    public async Task<AdminUserDto> PatchUserAsync(Guid userId, AdminUserPatch patch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patch);
        using var response = await SendAsync(
            HttpMethod.Patch, $"admin/users/{userId:D}", patch, anonymous: false, configure: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadJsonAsync<AdminUserDto>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null, anonymous: false, configure: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object body, bool anonymous, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, anonymous, configure: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool anonymous,
        Action<HttpRequestMessage>? configure,
        CancellationToken ct)
    {
        var uri = BuildUri(path);
        using var request = new HttpRequestMessage(method, uri);
        if (anonymous)
        {
            request.Options.Set(AuthenticatedHandler.AnonymousOption, true);
        }

        if (body is not null)
        {
            // Pre-serialized bytes so AuthenticatedHandler can re-send the body on its single retry.
            var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions));
            content.Headers.ContentType = JsonContentType;
            request.Content = content;
        }

        configure?.Invoke(request);

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "API {Method} {Path}: server unreachable", method, uri.AbsolutePath);
            throw new ApiUnavailableException("Could not reach the server: " + ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException without the caller's token being cancelled.
            _logger.LogWarning("API {Method} {Path}: timed out after {Timeout}s", method, uri.AbsolutePath, _http.Timeout.TotalSeconds);
            throw new ApiUnavailableException($"The server did not respond within {_http.Timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private Uri BuildUri(string path)
    {
        if (!ServerUrl.TryNormalize(_settings.Current.ServerUrl, out var baseUri))
        {
            throw new ApiUnavailableException("The server URL is not configured.");
        }

        return new Uri(baseUri, BasePath + path);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        string? message = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(text, JsonOptions);
                code = envelope?.Error?.Code;
                message = envelope?.Error?.Message;
            }
        }
        catch (JsonException)
        {
            // Not our envelope (proxy/captive portal page): fall back to the status code below.
        }

        code = string.IsNullOrWhiteSpace(code) ? DefaultCodeFor(response.StatusCode) : code;
        message = string.IsNullOrWhiteSpace(message) ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() : message;

        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
        _logger.LogWarning("API {Method} {Path}: {Status} {Code}", response.RequestMessage?.Method, path, (int)response.StatusCode, code);
        throw new ApiException(response.StatusCode, code, message);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            return value ?? throw new ApiException(response.StatusCode, ApiErrorCodes.InvalidResponse, "The server returned an empty body.");
        }
        catch (JsonException ex)
        {
            throw new ApiException(response.StatusCode, ApiErrorCodes.InvalidResponse, "The server returned malformed JSON.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ApiException(response.StatusCode, ApiErrorCodes.InvalidResponse, "The server returned a non-JSON body.", ex);
        }
    }

    private static string DefaultCodeFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ApiErrorCodes.Unauthorized,
        HttpStatusCode.Forbidden => ApiErrorCodes.Forbidden,
        HttpStatusCode.NotFound => ApiErrorCodes.NotFound,
        HttpStatusCode.Conflict => ApiErrorCodes.Conflict,
        HttpStatusCode.UnprocessableEntity => ApiErrorCodes.ValidationError,
        HttpStatusCode.Locked => ApiErrorCodes.AccountLocked,
        HttpStatusCode.TooManyRequests => ApiErrorCodes.RateLimited,
        _ => (int)status >= 500 ? ApiErrorCodes.Internal : ApiErrorCodes.HttpError,
    };
}
