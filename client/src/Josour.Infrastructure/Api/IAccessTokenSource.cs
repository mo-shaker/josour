namespace Josour.Infrastructure.Api;

/// <summary>What <see cref="AuthenticatedHandler"/> needs from the auth session: the current access token and a single-flight refresh.</summary>
public interface IAccessTokenSource
{
    /// <summary>Current access JWT, or null when not signed in.</summary>
    string? AccessToken { get; }

    /// <summary>
    /// Refreshes the access token at most once per rejected token: concurrent callers that pass the same <paramref name="rejectedToken"/>
    /// share one refresh, and a caller whose token was already replaced gets the newer token without a network call.
    /// Returns the token to retry with, or null when the session could not be refreshed (it has then been signed out).
    /// Throws <see cref="ApiUnavailableException"/> when the server could not be reached (the session stays signed in).
    /// </summary>
    Task<string?> RefreshAccessTokenAsync(string? rejectedToken, CancellationToken ct);
}
