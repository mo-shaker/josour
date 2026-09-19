using System.Text.Json.Serialization;

namespace Josour.Infrastructure.Api;

// DTOs of docs/api.md. Field names are the wire names (snake_case) via JsonPropertyName; times are DateTimeOffset (ISO-8601 UTC "Z");
// ids are Guid. Records that carry tokens or secrets override ToString so an accidental log call never prints them.

/// <summary><c>device</c> object of <c>POST /auth/login</c>. <see cref="Id"/> and <see cref="Secret"/> are null on the first login from a device.</summary>
public sealed record DeviceLoginInfo(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("secret")] string? Secret,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os_version")] string OsVersion,
    [property: JsonPropertyName("os_build")] string? OsBuild)
{
    public override string ToString() => $"DeviceLoginInfo {{ Id = {Id?.ToString() ?? "null"}, Secret = {(Secret is null ? "null" : "[redacted]")}, Name = {Name} }}";
}

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("device")] DeviceLoginInfo Device)
{
    public override string ToString() => $"LoginRequest {{ Email = {Email}, Password = [redacted], Device = {Device} }}";
}

public sealed record RefreshRequest([property: JsonPropertyName("refresh_token")] string RefreshToken)
{
    public override string ToString() => "RefreshRequest { RefreshToken = [redacted] }";
}

public sealed record LogoutRequest([property: JsonPropertyName("refresh_token")] string RefreshToken)
{
    public override string ToString() => "LogoutRequest { RefreshToken = [redacted] }";
}

public sealed record UserDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role);

/// <summary><c>device</c> object of the auth responses. <see cref="Secret"/> is returned exactly once, when the device was just created.</summary>
public sealed record AuthDeviceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("secret")] string? Secret)
{
    public override string ToString() => $"AuthDeviceDto {{ Id = {Id}, Name = {Name}, Secret = {(Secret is null ? "null" : "[redacted]")} }}";
}

/// <summary>Response of <c>POST /auth/login</c> and <c>POST /auth/refresh</c>.</summary>
public sealed record AuthResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("user")] UserDto User,
    [property: JsonPropertyName("device")] AuthDeviceDto Device)
{
    public override string ToString() => $"AuthResponse {{ AccessToken = [redacted], RefreshToken = [redacted], ExpiresIn = {ExpiresIn}, User = {User}, Device = {Device} }}";
}

/// <summary>Entry of <c>GET /me/devices</c>.</summary>
public sealed record DeviceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os_version")] string? OsVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>Entry of <c>GET /sessions/me</c>.</summary>
public sealed record SessionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("peer_display_name")] string PeerDisplayName,
    [property: JsonPropertyName("peer_device_name")] string PeerDeviceName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("end_reason")] string? EndReason,
    [property: JsonPropertyName("bytes_up")] long BytesUp,
    [property: JsonPropertyName("bytes_down")] long BytesDown);

/// <summary>Body of <c>GET /domains</c>.</summary>
public sealed record DomainsDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries);

/// <summary>Outcome of <c>GET /domains</c> with <c>If-None-Match</c>: either the new list or "not modified".</summary>
public sealed class DomainsResult
{
    private DomainsResult(DomainsDto? domains, string? etag)
    {
        Domains = domains;
        ETag = etag;
    }

    /// <summary>The server answered <c>304</c>: the version the client already has is current.</summary>
    public static DomainsResult NotModified { get; } = new(null, null);

    public static DomainsResult From(DomainsDto domains, string? etag) => new(domains ?? throw new ArgumentNullException(nameof(domains)), etag);

    public bool IsNotModified => Domains is null;

    public DomainsDto? Domains { get; }

    /// <summary>Raw <c>ETag</c> header value (the contract uses the quoted version number, e.g. <c>"3"</c>).</summary>
    public string? ETag { get; }
}

public sealed record ProbeRequest(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port);

/// <summary>
/// Body of <c>POST /diagnostics</c>. <see cref="Data"/> is a free JSON object capped at 64 KB serialized; the reserved keys
/// inside it are listed in docs/api.md and built by <c>Diagnostics.ListenerAuthDiagnostics</c> — nothing else goes in.
/// </summary>
public sealed record DiagnosticsRequest(
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data);

/// <summary>Response of <c>POST /diagnostics</c>: the id of the stored row.</summary>
public sealed record DiagnosticsAccepted([property: JsonPropertyName("id")] Guid Id);

/// <summary>Response of <c>POST /probe</c>; <see cref="LatencyMs"/> is null when unreachable.</summary>
public sealed record ProbeResult(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("latency_ms")] int? LatencyMs);

internal sealed record ApiErrorBody(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ApiErrorEnvelope([property: JsonPropertyName("error")] ApiErrorBody? Error);

// ---------------------------------------------------------------- admin (role = admin)

/// <summary>
/// One account as <c>GET /admin/users</c> returns it: <see cref="UserDto"/> plus the state an administrator acts on.
/// </summary>
/// <param name="IsActive">False means the account is disabled: it cannot sign in, and its live control channels
/// were closed when it was disabled.</param>
/// <param name="FailedLogins">Consecutive failed sign-ins; reset by an unlock.</param>
/// <param name="LockedUntil">Non-null while the account is locked out after repeated failures (ADR-0008).</param>
public sealed record AdminUserDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("failed_logins")] int FailedLogins,
    [property: JsonPropertyName("locked_until")] DateTimeOffset? LockedUntil,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>True while a lockout is still in force at <paramref name="now"/>.</summary>
    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

/// <summary>Body of <c>POST /admin/users</c>.</summary>
public sealed record AdminUserCreate(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role);

/// <summary>
/// Body of <c>PATCH /admin/users/{id}</c>. Every field is optional and omitted fields are left alone; the server
/// rejects unknown keys rather than ignoring them, so this record must not grow a property the contract lacks.
/// </summary>
public sealed record AdminUserPatch
{
    [JsonPropertyName("is_active")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsActive { get; init; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; init; }

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("unlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Unlock { get; init; }
}
