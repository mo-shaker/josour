using System.Text.Json.Serialization;

namespace Josour.Core.Control;

/// <summary>رسائل docs/ws-protocol.md. الحقول بأسماء JSON كما في العقد حرفيًا.</summary>
public abstract record ControlMessage([property: JsonPropertyName("type")] string Type);

// ---------- عميل ← خادم ----------
public sealed record HelloMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("diagnostics")] Dictionary<string, object?>? Diagnostics) : ControlMessage("hello");

public sealed record HostAvailableMessage(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("listen_port")] int? ListenPort) : ControlMessage("host.available");

public sealed record RequestCreateMessage(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("host_device_id")] Guid HostDeviceId,
    [property: JsonPropertyName("duration_min")] int DurationMin) : ControlMessage("request.create");

public sealed record RequestCancelMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("request_id")] Guid RequestId) : ControlMessage("request.cancel");
public sealed record RequestAcceptMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("request_id")] Guid RequestId) : ControlMessage("request.accept");
public sealed record RequestRejectMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("request_id")] Guid RequestId) : ControlMessage("request.reject");

public sealed record CandidateDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port);

public sealed record SessionEndpointMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("cert_fp_sha256")] string CertFpSha256,
    [property: JsonPropertyName("candidates")] IReadOnlyList<CandidateDto> Candidates) : ControlMessage("session.endpoint");

public sealed record SessionConnectedMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("winner_type")] string WinnerType,
    [property: JsonPropertyName("connect_ms")] int ConnectMs,
    [property: JsonPropertyName("tls_version")] string TlsVersion) : ControlMessage("session.connected");

public sealed record SessionConnectFailedMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("diagnostics")] Dictionary<string, object?> Diagnostics) : ControlMessage("session.connect_failed");

public sealed record SessionStatsMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("bytes_up")] long BytesUp,
    [property: JsonPropertyName("bytes_down")] long BytesDown) : ControlMessage("session.stats");

public sealed record SessionEndMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("bytes_up")] long BytesUp,
    [property: JsonPropertyName("bytes_down")] long BytesDown,
    [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains) : ControlMessage("session.end");

public sealed record PingMessage() : ControlMessage("ping");
public sealed record PongMessage() : ControlMessage("pong");

// ---------- خادم ← عميل ----------
public sealed record ServerSettings(
    [property: JsonPropertyName("max_session_minutes")] int MaxSessionMinutes,
    [property: JsonPropertyName("request_timeout_seconds")] int RequestTimeoutSeconds,
    [property: JsonPropertyName("allowed_ports")] IReadOnlyList<int> AllowedPorts,
    [property: JsonPropertyName("log_domains")] bool LogDomains);

public sealed record HelloAckMessage(
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("public_ip")] string PublicIp,
    [property: JsonPropertyName("settings")] ServerSettings Settings,
    [property: JsonPropertyName("allowlist_version")] int AllowlistVersion) : ControlMessage("hello.ack");

public sealed record HostInfoDto(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("user_display_name")] string UserDisplayName,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("reachable")] bool? Reachable);

public sealed record HostsMessage(string Type, [property: JsonPropertyName("hosts")] IReadOnlyList<HostInfoDto> Hosts) : ControlMessage(Type); // hosts.snapshot | hosts.update

public sealed record RequestCreatedMessage(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt) : ControlMessage("request.created");

public sealed record RequestIncomingMessage(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("guest_name")] string GuestName,
    [property: JsonPropertyName("guest_device")] string GuestDevice,
    [property: JsonPropertyName("duration_min")] int DurationMin,
    [property: JsonPropertyName("allowlist_version")] int AllowlistVersion,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt) : ControlMessage("request.incoming");

public sealed record RequestResultMessage(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("session_id")] Guid? SessionId) : ControlMessage("request.result");

public sealed record RequestExpiredMessage([property: JsonPropertyName("request_id")] Guid RequestId) : ControlMessage("request.expired");

public sealed record PeerDto(
    [property: JsonPropertyName("user_display_name")] string UserDisplayName,
    [property: JsonPropertyName("device_name")] string DeviceName);

public sealed record SessionCreatedMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("secret_b64")] string SecretB64,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("allowlist_version")] int AllowlistVersion,
    [property: JsonPropertyName("peer_public_ip")] string PeerPublicIp,
    [property: JsonPropertyName("same_public_ip")] bool SamePublicIp,
    [property: JsonPropertyName("peer")] PeerDto Peer,
    [property: JsonPropertyName("relay")] RelayDto? Relay = null) : ControlMessage("session.created");

/// <summary>
/// عنوان الـ Relay وتوكن هذا الطرف (ADR-0009). null في نشر بلا Relay، وهو مدعوم: المباشر وحده.
/// <see cref="Token"/> بيان حامل: لا يُسجَّل ولا يوضع في تشخيص.
/// </summary>
public sealed record RelayDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token);

public sealed record SessionPeerEndpointMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("cert_fp_sha256")] string CertFpSha256,
    [property: JsonPropertyName("candidates")] IReadOnlyList<CandidateDto> Candidates) : ControlMessage("session.peer_endpoint");

public sealed record SessionActiveMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt) : ControlMessage("session.active");

public sealed record SessionTerminateMessage(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("reason")] string Reason) : ControlMessage("session.terminate");

public sealed record AllowlistUpdatedMessage([property: JsonPropertyName("version")] int Version) : ControlMessage("allowlist.updated");

public sealed record ErrorMessage(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message) : ControlMessage("error");
