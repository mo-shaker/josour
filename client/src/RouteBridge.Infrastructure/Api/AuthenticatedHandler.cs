using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteBridge.Infrastructure.Api;

/// <summary>
/// Adds <c>Authorization: Bearer</c> to every non-anonymous request. On <c>401</c> it asks <see cref="IAccessTokenSource"/> for ONE
/// refresh (single-flight inside the source) and retries the request exactly once with the new token; if the refresh fails the
/// original 401 is returned (the source has raised its signed-out event) and if the server is unreachable during the refresh
/// <see cref="ApiUnavailableException"/> propagates. Requests flagged with <see cref="AnonymousOption"/> (login/refresh/logout) pass through untouched.
/// <para>The token source is resolved lazily (<see cref="Func{T}"/>) because the source itself calls the API through this handler.</para>
/// </summary>
public sealed class AuthenticatedHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> AnonymousOption = new("RouteBridge.Api.Anonymous");

    private const string BearerScheme = "Bearer";

    private readonly Func<IAccessTokenSource> _tokenSource;
    private readonly ILogger<AuthenticatedHandler> _logger;

    public AuthenticatedHandler(Func<IAccessTokenSource> tokenSource, ILogger<AuthenticatedHandler>? logger = null)
    {
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _logger = logger ?? NullLogger<AuthenticatedHandler>.Instance;
    }

    public AuthenticatedHandler(IAccessTokenSource tokenSource, ILogger<AuthenticatedHandler>? logger = null)
        : this(() => tokenSource, logger)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(AnonymousOption, out var anonymous) && anonymous)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var source = _tokenSource();
        var token = source.AccessToken;
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        _logger.LogInformation("401 from {Method} {Path}: refreshing the access token once", request.Method, request.RequestUri?.AbsolutePath);

        string? fresh;
        try
        {
            fresh = await source.RefreshAccessTokenAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (fresh is null)
        {
            _logger.LogInformation("Access token could not be refreshed; returning the 401 to the caller");
            return response;
        }

        var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        retry.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, fresh);
        response.Dispose();
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }
}
