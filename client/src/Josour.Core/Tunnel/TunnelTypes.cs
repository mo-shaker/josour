namespace Josour.Core.Tunnel;

/// <summary>الدور المنطقي في الجلسة. لا يتغير أيًا كان من اتصل على مستوى TCP.</summary>
public enum TunnelRole { Guest, Host }

public enum TunnelState { Idle, Listening, Connecting, Authenticating, Connected, Ended }

/// <summary>أسباب الإنهاء كما في docs/ws-protocol.md القسم 5.</summary>
public enum TunnelEndReason
{
    GuestEnded, HostEnded, Expired, GuestDisconnected, HostDisconnected,
    ConnectFailed, AdminTerminated, BrowserNotProxied, ProtocolError
}

/// <summary>نوع المرشح كما في docs/protocol.md القسم 2.</summary>
public enum CandidateType { Lan, V6, Upnp, Public }

public sealed record CandidateEndpoint(CandidateType Type, string Ip, int Port);

/// <summary>ما يصل من الخادم في session.peer_endpoint.</summary>
public sealed record PeerEndpointInfo(string CertFingerprintSha256Hex, IReadOnlyList<CandidateEndpoint> Candidates);

/// <summary>ما يرسله هذا الطرف في session.endpoint.</summary>
public sealed record LocalEndpointInfo(string CertFingerprintSha256Hex, IReadOnlyList<CandidateEndpoint> Candidates);

public sealed record TunnelConnectResult(bool Connected, CandidateType? WinnerType, int ConnectMs, string? TlsVersion, string? FailureReason);

public sealed record TunnelStats(long BytesUp, long BytesDown, int OpenStreams);

/// <summary>
/// ما يحتاجه التطبيق ليشغّل المتصفح على جانب Guest بعد نجاح الاتصال: منفذ الـ Proxy المحلي (127.0.0.1) ورابط صفحة الفحص.
/// إضافة الأسبوع 3 (المسار B) لأن المسار C يشغّل المتصفح بنفسه ويحتاج هذين الحقلين من الجلسة.
/// </summary>
public sealed record GuestProxyInfo(int Port, string ProbeUrl);

/// <summary>مواد الجلسة القادمة من session.created. تُمسح عند الإنهاء.</summary>
public sealed record SessionMaterial(Guid SessionId, TunnelRole Role, byte[] Secret, DateTimeOffset ExpiresAt, bool SamePublicIp, string PeerPublicIp);
