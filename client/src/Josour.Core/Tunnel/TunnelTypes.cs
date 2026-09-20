namespace Josour.Core.Tunnel;

/// <summary>The logical role in the session. It does not change, whoever connected at the TCP level.</summary>
public enum TunnelRole { Guest, Host }

public enum TunnelState { Idle, Listening, Connecting, Authenticating, Connected, Ended }

/// <summary>The end reasons as in docs/ws-protocol.md section 5.</summary>
public enum TunnelEndReason
{
    GuestEnded, HostEnded, Expired, GuestDisconnected, HostDisconnected,
    ConnectFailed, AdminTerminated, BrowserNotProxied, ProtocolError
}

/// <summary>The candidate's type as in docs/protocol.md section 2.</summary>
/// <summary>
/// The path's type. The first four are candidates announced in session.endpoint and connected to; <see cref="Relay"/> is not a candidate:
/// it is never sent in session.endpoint (the server refuses it), and its address arrives in session.created.relay. But it is a valid value
/// for winner_type, because it is a correct answer to "what carried this session" (ADR-0009).
/// </summary>
public enum CandidateType { Lan, V6, Upnp, Public, Relay }

public sealed record CandidateEndpoint(CandidateType Type, string Ip, int Port);

/// <summary>What arrives from the server in session.peer_endpoint.</summary>
public sealed record PeerEndpointInfo(string CertFingerprintSha256Hex, IReadOnlyList<CandidateEndpoint> Candidates);

/// <summary>What this side sends in session.endpoint.</summary>
public sealed record LocalEndpointInfo(string CertFingerprintSha256Hex, IReadOnlyList<CandidateEndpoint> Candidates);

public sealed record TunnelConnectResult(bool Connected, CandidateType? WinnerType, int ConnectMs, string? TlsVersion, string? FailureReason);

public sealed record TunnelStats(long BytesUp, long BytesDown, int OpenStreams);

/// <summary>
/// What the application needs to launch the browser on the guest side once the connection succeeds: the local proxy's port (127.0.0.1) and the check page's URL.
/// Added in week 3 (track B) because track C launches the browser itself and needs these two fields from the session.
/// </summary>
public sealed record GuestProxyInfo(int Port, string ProbeUrl);

/// <summary>The session material coming from session.created. Wiped at the end.</summary>
public sealed record SessionMaterial(Guid SessionId, TunnelRole Role, byte[] Secret, DateTimeOffset ExpiresAt, bool SamePublicIp, string PeerPublicIp);

/// <summary>
/// The relay's address and this side's token, as they arrive in <c>session.created.relay</c> (ADR-0009).
/// <see cref="Token"/> is a short-lived bearer statement bound to the session and the role: it is not logged and not put into any diagnostics.
/// </summary>
public sealed record RelayEndpointInfo(string Address, int Port, string Token);
