using RouteBridge.Infrastructure.Api;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// A signed-in (or signed-out) session with no server behind it. It deliberately carries a token value so that a test can
/// prove the token does NOT appear where it must not.
/// </summary>
public sealed class FakeAuthSession : IAuthSession
{
    public bool IsSignedIn => CurrentUser is not null;

    public UserDto? CurrentUser { get; set; }

    public Guid? DeviceId { get; set; }

    public string? AccessToken { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    /// <summary>What <see cref="HasStoredCredentialsAsync"/> answers.</summary>
    public bool StoredCredentials { get; set; }

    public event EventHandler? Changed;

    public event EventHandler<SignedOutEventArgs>? SignedOut;

    public Task<bool> HasStoredCredentialsAsync(CancellationToken ct) => Task.FromResult(StoredCredentials);

    public Task<bool> TryRestoreAsync(CancellationToken ct) => Task.FromResult(IsSignedIn);

    public Task SignInAsync(string email, string password, CancellationToken ct) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken ct)
    {
        CurrentUser = null;
        Changed?.Invoke(this, EventArgs.Empty);
        SignedOut?.Invoke(this, new SignedOutEventArgs(SignOutReason.UserRequested));
        return Task.CompletedTask;
    }

    public Task ForceSignOutAsync(SignOutReason reason, CancellationToken ct)
    {
        CurrentUser = null;
        SignedOut?.Invoke(this, new SignedOutEventArgs(reason));
        return Task.CompletedTask;
    }

    public Task<string?> GetValidAccessTokenAsync(CancellationToken ct) => Task.FromResult(AccessToken);

    /// <summary>A session signed in as a sample user, with a device id and an access token in memory.</summary>
    public static FakeAuthSession SignedInAs(string email = "guest@example.com", string? accessToken = "at-secret-value-0123456789") => new()
    {
        CurrentUser = new UserDto(new Guid("11111111-0000-4000-8000-000000000001"), email, "Guest", "user"),
        DeviceId = new Guid("bbbbbbbb-0000-4000-8000-000000000002"),
        AccessToken = accessToken,
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        StoredCredentials = true,
    };
}
