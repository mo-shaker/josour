using Josour.Core.Control;

namespace Josour.Infrastructure.Api;

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

    /// <summary><c>GET /auth/registration</c>. Anonymous. Whether this server lets people create their own account.</summary>
    Task<RegistrationInfoDto> GetRegistrationAsync(CancellationToken ct);

    /// <summary>
    /// <c>POST /auth/register</c>. Anonymous. Never signs in: on <c>active</c> call <see cref="LoginAsync"/>; on
    /// <c>pending</c> an administrator must approve the account first (and a sign-in answers <c>account_pending</c>).
    /// </summary>
    Task<RegisterResponse> RegisterAsync(string email, string password, string displayName, CancellationToken ct);

    // ---------------------------------------------------------------- contacts (ADR-0014)

    /// <summary><c>GET /contacts</c>.</summary>
    Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct);

    /// <summary><c>DELETE /contacts/{id}</c> → 204. Ends a session between the two, if one is running.</summary>
    Task RemoveContactAsync(Guid userId, CancellationToken ct);

    /// <summary><c>PUT /contacts/{id}/trust</c>: let this contact use my connection without asking me (or stop).</summary>
    Task<ContactDto> SetTrustAsync(Guid userId, bool autoAccept, CancellationToken ct);

    /// <summary><c>GET /contacts/invites</c>.</summary>
    Task<InvitesDto> GetInvitesAsync(CancellationToken ct);

    /// <summary><c>POST /contacts/invites</c> → 202, whatever the address (the answer never says who has an account).</summary>
    Task InviteAsync(string email, CancellationToken ct);

    /// <summary><c>POST /contacts/invites/{id}/accept</c>: the contact that now exists.</summary>
    Task<ContactDto> AcceptInviteAsync(Guid inviteId, CancellationToken ct);

    /// <summary><c>POST /contacts/invites/{id}/decline</c> → 204. The inviter is not told.</summary>
    Task DeclineInviteAsync(Guid inviteId, CancellationToken ct);

    /// <summary><c>DELETE /contacts/invites/{id}</c> → 204: withdraw an invitation I sent.</summary>
    Task CancelInviteAsync(Guid inviteId, CancellationToken ct);

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

    /// <summary>
    /// <c>GET /domains?version=N</c>: exactly that version of the allow-list, whatever the current one is (docs/api.md).
    /// The host's pre-accept disclosure needs it, because <c>request.incoming</c> names the version the guest's session
    /// will run under and showing a different list would be a lie. A version the server no longer has answers
    /// <c>404</c> → <see cref="ApiException"/> with <see cref="ApiErrorCodes.NotFound"/>.
    /// </summary>
    Task<DomainsDto> GetDomainsVersionAsync(int version, CancellationToken ct);

    /// <summary><c>POST /probe</c>.</summary>
    Task<ProbeResult> ProbeAsync(string ip, int port, CancellationToken ct);

    /// <summary>
    /// <c>POST /diagnostics</c> → <c>201 { id }</c>. <paramref name="data"/> is stored as-is (64 KB limit) and may carry the
    /// reserved keys of docs/api.md; <paramref name="sessionId"/> must name a session this device is a party to, or the
    /// server answers <c>404</c>/<c>403</c>.
    /// </summary>
    Task<DiagnosticsAccepted> PostDiagnosticsAsync(Guid? sessionId, string? role, IReadOnlyDictionary<string, object?> data, CancellationToken ct);

    // ---------------------------------------------------------------- admin (role = admin)
    // Every one of these answers 403 for a non-admin token. The app only shows the panel to an
    // administrator, but that is a courtesy to the user: the server is what enforces it.

    /// <summary><c>GET /admin/users</c>, newest first. <paramref name="query"/> filters by email or display name.</summary>
    Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(string? query, CancellationToken ct);

    /// <summary><c>POST /admin/users</c> → <c>201</c> with the created account. <c>409</c> when the email is taken.</summary>
    Task<AdminUserDto> CreateUserAsync(AdminUserCreate request, CancellationToken ct);

    /// <summary>
    /// <c>PATCH /admin/users/{id}</c>. Omitted fields are left alone; unknown ones are rejected by the server.
    /// <para>
    /// <c>IsActive = false</c> is not only a flag: the server revokes the account's refresh tokens, clears its
    /// presence and closes its live control channels with 4403. It takes effect at once.
    /// </para>
    /// </summary>
    Task<AdminUserDto> PatchUserAsync(Guid userId, AdminUserPatch patch, CancellationToken ct);
}
