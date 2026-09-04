using RouteBridge.Core.Control;

namespace RouteBridge.Infrastructure.Control.Mock;

/// <summary>
/// Timing and fixtures of <see cref="MockControlChannel"/>. Delays are real time on the given <see cref="TimeProvider"/>;
/// tests use <see cref="Fast"/>. IPs are TEST-NET placeholders (RFC 5737).
/// </summary>
public sealed record MockControlChannelOptions
{
    public static readonly Guid ReachableHostId = new("11111111-1111-4111-8111-111111111111");
    public static readonly Guid UnknownReachabilityHostId = new("22222222-2222-4222-8222-222222222222");

    public static IReadOnlyList<HostInfoDto> DefaultHosts { get; } = new[]
    {
        new HostInfoDto(ReachableHostId, "Omar Hassan", "OMAR-DESKTOP", Reachable: true),
        new HostInfoDto(UnknownReachabilityHostId, "Lina Yousef", "LINA-LAPTOP", Reachable: null),
    };

    public static MockControlChannelOptions Default { get; } = new();

    /// <summary>Every delay 1 ms: for unit tests.</summary>
    public static MockControlChannelOptions Fast { get; } = new()
    {
        ConnectDelay = TimeSpan.FromMilliseconds(1),
        SnapshotDelay = TimeSpan.FromMilliseconds(1),
        HostsUpdateDelay = TimeSpan.FromMilliseconds(1),
        RequestDecisionDelay = TimeSpan.FromMilliseconds(1),
        PeerEndpointDelay = TimeSpan.FromMilliseconds(1),
        ActiveDelay = TimeSpan.FromMilliseconds(1),
    };

    // ---- timing ----
    public TimeSpan ConnectDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary><c>hosts.snapshot</c> after <c>hello.ack</c>.</summary>
    public TimeSpan SnapshotDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary><c>hosts.update</c> after <c>host.available</c>.</summary>
    public TimeSpan HostsUpdateDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary><c>request.result</c> after <c>request.created</c> (the fake host "thinking").</summary>
    public TimeSpan RequestDecisionDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary><c>session.peer_endpoint</c> after the client's <c>session.endpoint</c>.</summary>
    public TimeSpan PeerEndpointDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Guest role only: <c>session.active</c> after <c>session.peer_endpoint</c> (the fake host reporting <c>session.connected</c>).</summary>
    public TimeSpan ActiveDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    // ---- behaviour ----

    /// <summary>The fake host accepts every <c>request.create</c>; false → <c>request.result {accepted:false, reason:"rejected"}</c>.</summary>
    public bool AutoAcceptGuestRequests { get; init; } = true;

    /// <summary>Guest role: emit <c>session.active</c> on the fake host's behalf once the endpoints were exchanged.</summary>
    public bool AutoActivateGuestSession { get; init; } = true;

    // ---- fixtures ----
    public string PublicIp { get; init; } = "203.0.113.10";

    public string PeerPublicIp { get; init; } = "203.0.113.10";

    public int PeerCandidatePort { get; init; } = 40000;

    public bool SamePublicIp { get; init; }

    /// <summary>64 lowercase hex characters, like a real SHA-256 certificate fingerprint.</summary>
    public string PeerCertFingerprint { get; init; } = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    public int AllowlistVersion { get; init; } = 1;

    public ServerSettings Settings { get; init; } = new(MaxSessionMinutes: 120, RequestTimeoutSeconds: 60, AllowedPorts: new[] { 80, 443 }, LogDomains: false);

    public IReadOnlyList<HostInfoDto> FakeHosts { get; init; } = DefaultHosts;

    /// <summary>How this device appears in <c>hosts.update</c> while it is available as a host.</summary>
    public string SelfUserDisplayName { get; init; } = "You";

    public string SelfDeviceName { get; init; } = "This device";

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Settings.RequestTimeoutSeconds);
}
