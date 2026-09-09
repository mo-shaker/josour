namespace Josour.Infrastructure.Api;

public enum SignOutReason
{
    /// <summary>The user chose Sign out.</summary>
    UserRequested,

    /// <summary>The refresh token was rejected (expired, rotated elsewhere, or revoked).</summary>
    SessionExpired,

    /// <summary>The server answered <c>device_revoked</c>.</summary>
    DeviceRevoked,

    /// <summary>The server answered <c>account_disabled</c>.</summary>
    AccountDisabled,
}

public sealed class SignedOutEventArgs : EventArgs
{
    public SignedOutEventArgs(SignOutReason reason)
    {
        Reason = reason;
    }

    public SignOutReason Reason { get; }
}

/// <summary>
/// The signed-in state of the app: access token in memory, refresh token + device identity in <see cref="Core.Security.ISecretStore"/>.
/// Events may be raised on background threads; UI code must marshal.
/// </summary>
public interface IAuthSession
{
    bool IsSignedIn { get; }

    UserDto? CurrentUser { get; }

    /// <summary>The device id the server knows this installation by (from the last auth response); null when signed out.</summary>
    Guid? DeviceId { get; }

    string? AccessToken { get; }

    /// <summary>When the current access token expires (server's <c>expires_in</c> applied to the local clock); null when signed out.</summary>
    DateTimeOffset? AccessTokenExpiresAt { get; }

    /// <summary>Raised after any change of <see cref="IsSignedIn"/>, <see cref="CurrentUser"/> or the tokens.</summary>
    event EventHandler? Changed;

    /// <summary>Raised once whenever the session goes from signed-in to signed-out (explicitly or because a refresh failed).</summary>
    event EventHandler<SignedOutEventArgs>? SignedOut;

    /// <summary>Silent sign-in with the stored refresh token. False when there is none, it was rejected (secrets cleared) or the server is unreachable (secrets kept).</summary>
    Task<bool> TryRestoreAsync(CancellationToken ct);

    /// <summary>
    /// Whether this machine has ever been signed in, i.e. a refresh token is on disk — even one the server would now
    /// reject. It is how the start-up tells a brand-new install from one whose session merely expired
    /// (<c>Settings.StartupPlanner</c>); it never says the token is still good, and it reads no token value.
    /// </summary>
    Task<bool> HasStoredCredentialsAsync(CancellationToken ct);

    /// <summary>Interactive sign-in. Registers the device on first use (stores the one-time device secret); throws <see cref="ApiException"/> / <see cref="ApiUnavailableException"/>.</summary>
    Task SignInAsync(string email, string password, CancellationToken ct);

    /// <summary>Best-effort <c>POST /auth/logout</c>, then clears the tokens locally in every case.</summary>
    Task SignOutAsync(CancellationToken ct);

    /// <summary>
    /// Clears the session locally because the server ended it (control-channel close <c>4403</c>, a rejected token…),
    /// without calling <c>POST /auth/logout</c>. Raises <see cref="SignedOut"/> with <paramref name="reason"/>.
    /// </summary>
    Task ForceSignOutAsync(SignOutReason reason, CancellationToken ct);

    /// <summary>Returns a token that is not about to expire, refreshing it first when needed (for the WebSocket <c>hello</c>). Null when signed out.</summary>
    Task<string?> GetValidAccessTokenAsync(CancellationToken ct);
}
