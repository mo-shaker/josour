using System.Text.Json.Serialization;

namespace Josour.Core.Control;

/// <summary>The messages of docs/ws-protocol.md. The fields carry the JSON names from the contract, literally.</summary>
public abstract record ControlMessage([property: JsonPropertyName("type")] string Type);

// ---------- client -> server ----------
public sealed record HelloMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("diagnostics")] Dictionary<string, object?>? Diagnostics,
    /// <summary>What this build understands (docs/ws-protocol.md section 10); see <see cref="ClientCapabilities"/>.</summary>
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null) : ControlMessage("hello");

/// <summary>The <c>hello.capabilities</c> names this build declares (docs/ws-protocol.md section 10).</summary>
public static class ClientCapabilities
{
    /// <summary>Understands <c>contacts.changed</c>, <c>contacts.presence</c> and <c>host.state</c>.</summary>
    public const string Contacts = "contacts";

    /// <summary>Can be rung by <c>offer.incoming</c>.</summary>
    public const string Offers = "offers";

    /// <summary>Bases an automatic acceptance on the trust the server holds (<c>request.incoming.trusted</c>).</summary>
    public const string TrustV2 = "trust_v2";

    /// <summary>Routes the guest's traffic through the system proxy rather than a work browser (ADR-0015).</summary>
    public const string SystemProxy = "system_proxy";

    /// <summary>What this build sends.</summary>
    public static IReadOnlyList<string> Current { get; } = [Contacts, Offers, TrustV2, SystemProxy];
}

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

/// <summary><c>offer.create</c>: this device offers its connection to a contact (docs/ws-protocol.md section 10).</summary>
public sealed record OfferCreateMessage(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("guest_user_id")] Guid GuestUserId,
    [property: JsonPropertyName("duration_min")] int DurationMin) : ControlMessage("offer.create");

public sealed record OfferCancelMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("offer_id")] Guid OfferId) : ControlMessage("offer.cancel");
public sealed record OfferAcceptMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("offer_id")] Guid OfferId) : ControlMessage("offer.accept");
public sealed record OfferRejectMessage([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("offer_id")] Guid OfferId) : ControlMessage("offer.reject");

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
    /// ADR-0010. false (the default) routes every site the guest's browsers ask for through the host; the
    /// allow-list is then an optional restriction the operator can switch on. Defaulted here so a client
    /// talking to a server that predates the field behaves as the decision says, not as the old code did.
    /// </summary>
    [property: JsonPropertyName("enforce_allowlist")] bool EnforceAllowlist = false);

public sealed record HelloAckMessage(
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("public_ip")] string PublicIp,
    [property: JsonPropertyName("settings")] ServerSettings Settings,
    [property: JsonPropertyName("allowlist_version")] int AllowlistVersion,
    /// <summary>What the server implements (docs/ws-protocol.md section 10); null from a server that predates contacts.</summary>
    [property: JsonPropertyName("features")] IReadOnlyList<string>? Features = null) : ControlMessage("hello.ack");

public sealed record HostInfoDto(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("user_display_name")] string UserDisplayName,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("reachable")] bool? Reachable,
    /// <summary>The contact the device belongs to; <see cref="Guid.Empty"/> from a server that predates contacts.</summary>
    [property: JsonPropertyName("user_id")] Guid UserId = default);

/// <summary><c>host.state</c>: what the server recorded about this device's availability (a host never sees itself in the host list).</summary>
public sealed record HostStateMessage(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reachable")] bool? Reachable) : ControlMessage("host.state");

/// <summary><c>contacts.changed</c>: re-read the contacts and invitations over REST.</summary>
public sealed record ContactsChangedMessage() : ControlMessage("contacts.changed");

public sealed record ContactPresenceDto(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("online")] bool Online);

/// <summary><c>contacts.presence</c>: every contact when <see cref="Full"/> (after hello), otherwise only those that changed.</summary>
public sealed record ContactsPresenceMessage(
    [property: JsonPropertyName("full")] bool Full,
    [property: JsonPropertyName("contacts")] IReadOnlyList<ContactPresenceDto> Contacts) : ControlMessage("contacts.presence");

public sealed record OfferCreatedMessage(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("offer_id")] Guid OfferId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt) : ControlMessage("offer.created");

/// <summary><c>offer.incoming</c>: a contact offers this user their connection.</summary>
public sealed record OfferIncomingMessage(
    [property: JsonPropertyName("offer_id")] Guid OfferId,
    [property: JsonPropertyName("host_user_id")] Guid HostUserId,
    [property: JsonPropertyName("host_device_id")] Guid HostDeviceId,
    [property: JsonPropertyName("host_name")] string HostName,
    [property: JsonPropertyName("host_device")] string HostDevice,
    [property: JsonPropertyName("duration_min")] int DurationMin,
    [property: JsonPropertyName("allowlist_version")] int AllowlistVersion,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt) : ControlMessage("offer.incoming");

/// <summary><c>offer.result</c>: to the offering device, how its offer ended.</summary>
public sealed record OfferResultMessage(
    [property: JsonPropertyName("offer_id")] Guid OfferId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("session_id")] Guid? SessionId) : ControlMessage("offer.result");

/// <summary><c>offer.closed</c>: to the guest's devices, stop showing this offer.</summary>
public sealed record OfferClosedMessage(
    [property: JsonPropertyName("offer_id")] Guid OfferId,
    [property: JsonPropertyName("reason")] string Reason) : ControlMessage("offer.closed");

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
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    /// <summary>
    /// The host told the server this contact may use the connection without asking (ADR-0014). The app answers without a
    /// window only when this is true <em>and</em> its own copy of the contact list agrees.
    /// </summary>
    [property: JsonPropertyName("trusted")] bool Trusted = false) : ControlMessage("request.incoming");

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
    [property: JsonPropertyName("relay")] RelayDto? Relay = null,
    /// <summary>Who started it: <c>guest</c> (a request) or <c>host</c> (an offer).</summary>
    [property: JsonPropertyName("initiator")] string Initiator = "guest") : ControlMessage("session.created");

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
