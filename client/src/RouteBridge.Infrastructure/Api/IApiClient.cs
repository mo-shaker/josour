using RouteBridge.Core.Control;

namespace RouteBridge.Infrastructure.Api;

/// <summary>
/// REST client for docs/api.md (<c>/api/v1</c>). Every call throws <see cref="ApiException"/> for a server-side error envelope and
/// <see cref="ApiUnavailableException"/> when the server cannot be reached; <see cref="OperationCanceledException"/> only for the caller's token.
/// Authenticated calls carry the Bearer token and get one transparent refresh + retry on 401 (see <see cref="AuthenticatedHandler"/>).
/// </summary>
public interface IApiClient
{
    /// <summary><c>POST /auth/login</c>. Anonymous.</summary>
    Task<AuthResponse> LoginAsync(string email, string password, DeviceLoginInfo device, CancellationToken ct);

    /// <summary><c>POST /auth/refresh</c>. Anonymous. The returned refresh token replaces the one sent (rotation).</summary>
    Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary><c>POST /auth/logout</c> → 204. Anonymous.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken ct);

    /// <summary><c>GET /me</c>.</summary>
    Task<UserDto> GetMeAsync(CancellationToken ct);

    /// <summary><c>GET /me/devices</c>.</summary>
    Task<IReadOnlyList<DeviceDto>> GetMyDevicesAsync(CancellationToken ct);

    /// <summary><c>DELETE /me/devices/{id}</c> → 204.</summary>
    Task DeleteMyDeviceAsync(Guid deviceId, CancellationToken ct);

    /// <summary><c>GET /hosts</c>; same shape as <c>hosts.snapshot</c>.</summary>
    Task<IReadOnlyList<HostInfoDto>> GetHostsAsync(CancellationToken ct);

    /// <summary><c>GET /sessions/me?limit=N</c>.</summary>
    Task<IReadOnlyList<SessionDto>> GetMySessionsAsync(int limit, CancellationToken ct);

    /// <summary><c>GET /domains</c>; sends <c>If-None-Match: "&lt;knownVersion&gt;"</c> and maps 304 to <see cref="DomainsResult.NotModified"/>.</summary>
    Task<DomainsResult> GetDomainsAsync(int? knownVersion, CancellationToken ct);

    /// <summary><c>POST /probe</c>.</summary>
    Task<ProbeResult> ProbeAsync(string ip, int port, CancellationToken ct);
}
