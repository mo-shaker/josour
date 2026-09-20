using System.Text.Json.Serialization;

namespace Josour.Core.Control;

/// <summary>The messages of docs/ws-protocol.md. The fields carry the JSON names from the contract, literally.</summary>
public abstract record ControlMessage([property: JsonPropertyName("type")] string Type);

// ---------- client -> server ----------
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
/// <summary>
/// <c>request.accept</c>. <paramref name="Auto"/> declares that this device matched the request against a trusted-guest
/// rule the host had set up beforehand and answered without showing a prompt (docs/ws-protocol.md section 5a). It changes
/// nothing about how the server settles the request — the acceptance is the host's either way — and exists so the audit
/// trail can tell an acceptance nobody watched from one somebody did.
/// </summary>
public sealed record RequestAcceptMessage(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("auto")] bool Auto = false) : ControlMessage("request.accept");
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

// ---------- server -> client ----------
public sealed record ServerSettings(
    [property: JsonPropertyName("max_session_minutes")] int MaxSessionMinutes,
    [property: JsonPropertyName("request_timeout_seconds")] int RequestTimeoutSeconds,
    [property: JsonPropertyName("allowed_ports")] IReadOnlyList<int> AllowedPorts,
    [property: JsonPropertyName("log_domains")] bool LogDomains,
    /// <summary>
    /// ADR-0010. false (the default) routes every site the work browser asks for through the host; the
    /// allow-list is then an optional restriction the operator can switch on. Defaulted here so a client
    /// talking to a server that predates the field behaves as the decision says, not as the old code did.
    /// </summary>
    [property: JsonPropertyName("enforce_allowlist")] bool EnforceAllowlist = false);

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
    /// <summary>
    /// The pair a trusted-guest rule is keyed on (docs/ws-protocol.md section 5a). <see cref="GuestName"/> and
    /// <see cref="GuestDevice"/> are chosen by the guest and may repeat or change; these two never do. A server that
    /// predates the field leaves them <see cref="Guid.Empty"/>, which <c>AutoAcceptPolicy</c> refuses to match on.
    /// </summary>
    [property: JsonPropertyName("guest_user_id")] Guid GuestUserId,
    [property: JsonPropertyName("guest_device_id")] Guid GuestDeviceId,
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

/// <summary>
/// The other party of a session. <see cref="UserId"/> / <see cref="DeviceId"/> are the same identity
/// <c>request.incoming</c> carries, so a host can trust the guest it has just finished a session with.
/// </summary>
public sealed record PeerDto(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
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
/// The relay's address and this side's token (ADR-0009). null in a deployment with no relay, which is supported: direct only.
/// <see cref="Token"/> is a bearer statement: it is not logged and not put into any diagnostics.
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
