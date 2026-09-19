using Josour.Core.Control;
using Josour.Infrastructure.Api;

namespace Josour.App.Tests;

/// <summary>
/// An <see cref="IApiClient"/> that refuses everything it was not asked about. Tests override the two or three
/// calls they exercise; anything else throwing is the point — a test that silently used a call it did not mean to
/// is a test that proves less than it claims.
/// </summary>
public abstract class StubApiClient : IApiClient
{
    private static T NotUsed<T>() => throw new NotSupportedException("This call was not expected by the test.");

    public virtual Task<AuthResponse> LoginAsync(string email, string password, DeviceLoginInfo device, CancellationToken ct) => NotUsed<Task<AuthResponse>>();
    public virtual Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct) => NotUsed<Task<AuthResponse>>();
    public virtual Task LogoutAsync(string refreshToken, CancellationToken ct) => NotUsed<Task>();
    public virtual Task<UserDto> GetMeAsync(CancellationToken ct) => NotUsed<Task<UserDto>>();
    public virtual Task<IReadOnlyList<DeviceDto>> GetMyDevicesAsync(CancellationToken ct) => NotUsed<Task<IReadOnlyList<DeviceDto>>>();
    public virtual Task DeleteMyDeviceAsync(Guid deviceId, CancellationToken ct) => NotUsed<Task>();
    public virtual Task<IReadOnlyList<HostInfoDto>> GetHostsAsync(CancellationToken ct) => NotUsed<Task<IReadOnlyList<HostInfoDto>>>();
    public virtual Task<IReadOnlyList<SessionDto>> GetMySessionsAsync(int limit, CancellationToken ct) => NotUsed<Task<IReadOnlyList<SessionDto>>>();
    public virtual Task<DomainsResult> GetDomainsAsync(int? knownVersion, CancellationToken ct) => NotUsed<Task<DomainsResult>>();
    public virtual Task<DomainsDto> GetDomainsVersionAsync(int version, CancellationToken ct) => NotUsed<Task<DomainsDto>>();
    public virtual Task<ProbeResult> ProbeAsync(string ip, int port, CancellationToken ct) => NotUsed<Task<ProbeResult>>();
    public virtual Task<DiagnosticsAccepted> PostDiagnosticsAsync(Guid? sessionId, string? role, IReadOnlyDictionary<string, object?> data, CancellationToken ct) => NotUsed<Task<DiagnosticsAccepted>>();
    public virtual Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(string? query, CancellationToken ct) => NotUsed<Task<IReadOnlyList<AdminUserDto>>>();
    public virtual Task<AdminUserDto> CreateUserAsync(AdminUserCreate request, CancellationToken ct) => NotUsed<Task<AdminUserDto>>();
    public virtual Task<AdminUserDto> PatchUserAsync(Guid userId, AdminUserPatch patch, CancellationToken ct) => NotUsed<Task<AdminUserDto>>();
}

/// <summary>A signed-in session with a fixed user; enough for anything that only reads the role or the id.</summary>
public sealed class StubAuthSession : IAuthSession
{
    public StubAuthSession(UserDto? user) => CurrentUser = user;

    public UserDto? CurrentUser { get; }

    public bool IsSignedIn => CurrentUser is not null;

    public Guid? DeviceId => null;

    public string? AccessToken => null;

    public DateTimeOffset? AccessTokenExpiresAt => null;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler<SignedOutEventArgs>? SignedOut
    {
        add { }
        remove { }
    }

    public Task<bool> TryRestoreAsync(CancellationToken ct) => Task.FromResult(IsSignedIn);
    public Task<bool> HasStoredCredentialsAsync(CancellationToken ct) => Task.FromResult(IsSignedIn);
    public Task SignInAsync(string email, string password, CancellationToken ct) => Task.CompletedTask;
    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ForceSignOutAsync(SignOutReason reason, CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetValidAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>A clock that does not move, so an expiry test asserts a rule rather than a race.</summary>
public sealed class FixedTime : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTime(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
