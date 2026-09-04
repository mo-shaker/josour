using System.Text.Json;
using RouteBridge.Core.Control;

namespace RouteBridge.Infrastructure.Tests;

public sealed class ControlMessageSerializerTests
{
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid HostId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid RequestId = new("cccccccc-0000-4000-8000-000000000003");
    private static readonly Guid SessionId = new("dddddddd-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset T = new(2026, 9, 4, 10, 15, 30, 123, TimeSpan.Zero);
    private const string Fp = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <summary>One instance of every record in ControlMessages.cs (both directions), with its wire type.</summary>
    public static TheoryData<ControlMessage, string> Samples()
    {
        var candidates = new[] { new CandidateDto("lan", "192.168.1.10", 45000), new CandidateDto("public", "203.0.113.10", 40000) };
        var hosts = new[] { new HostInfoDto(HostId, "Omar Hassan", "OMAR-DESKTOP", true), new HostInfoDto(Guid.NewGuid(), "Lina", "LINA-LAPTOP", null) };

        return new TheoryData<ControlMessage, string>
        {
            // client → server
            { new HelloMessage("jwt", DeviceId, "0.2.0", new Dictionary<string, object?> { ["firewall_rule_present"] = true, ["os_build"] = "22631", ["vpn_adapter"] = null, ["ipv6_global"] = false }), "hello" },
            { new HelloMessage("jwt", DeviceId, "0.2.0", null), "hello" },
            { new HostAvailableMessage(true, 45000), "host.available" },
            { new HostAvailableMessage(false, null), "host.available" },
            { new RequestCreateMessage("c1", HostId, 30), "request.create" },
            { new RequestCancelMessage("c2", RequestId), "request.cancel" },
            { new RequestAcceptMessage("c3", RequestId), "request.accept" },
            { new RequestRejectMessage("c4", RequestId), "request.reject" },
            { new SessionEndpointMessage(SessionId, Fp, candidates), "session.endpoint" },
            { new SessionConnectedMessage(SessionId, "lan", 87, "1.3"), "session.connected" },
            { new SessionConnectFailedMessage(SessionId, new Dictionary<string, object?> { ["tried"] = new[] { "lan", "public" }, ["lan_error"] = "timeout" }), "session.connect_failed" },
            { new SessionStatsMessage(SessionId, 1024, 2048), "session.stats" },
            { new SessionEndMessage(SessionId, "guest_ended", 10, 20, new[] { "example.com", "portal.corp:8443" }), "session.end" },
            { new SessionEndMessage(SessionId, "host_ended", 0, 0, Array.Empty<string>()), "session.end" },
            { new PingMessage(), "ping" },
            { new PongMessage(), "pong" },

            // server → client
            { new HelloAckMessage(T, "203.0.113.10", new ServerSettings(120, 60, new[] { 80, 443 }, false), 3), "hello.ack" },
            { new HostsMessage("hosts.snapshot", hosts), "hosts.snapshot" },
            { new HostsMessage("hosts.update", Array.Empty<HostInfoDto>()), "hosts.update" },
            { new RequestCreatedMessage("c1", RequestId, T), "request.created" },
            { new RequestIncomingMessage(RequestId, "Sara Ahmed", "SARA-LAPTOP", 30, 3, T), "request.incoming" },
            { new RequestResultMessage(RequestId, true, null, SessionId), "request.result" },
            { new RequestResultMessage(RequestId, false, "rejected", null), "request.result" },
            { new RequestExpiredMessage(RequestId), "request.expired" },
            { new SessionCreatedMessage(SessionId, "guest", Convert.ToBase64String(new byte[32]), T, 3, "203.0.113.10", false, new PeerDto("Omar Hassan", "OMAR-DESKTOP")), "session.created" },
            { new SessionPeerEndpointMessage(SessionId, Fp, candidates), "session.peer_endpoint" },
            { new SessionActiveMessage(SessionId, T), "session.active" },
            { new SessionTerminateMessage(SessionId, "host_ended"), "session.terminate" },
            { new AllowlistUpdatedMessage(4), "allowlist.updated" },
            { new ErrorMessage("c1", "host_unavailable", "The host went away"), "error" },
            { new ErrorMessage(null, "internal", "boom"), "error" },
        };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void RoundTrip_EveryMessageType_BothDirections(ControlMessage original, string wireType)
    {
        var json = ControlMessageSerializer.Serialize(original);

        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(wireType, document.RootElement.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        var parsed = ControlMessageSerializer.Deserialize(json);

        Assert.IsType(original.GetType(), parsed);
        Assert.Equal(wireType, parsed.Type);
        Assert.Equal(json, ControlMessageSerializer.Serialize(parsed)); // structural equality: every field survived

        var fromBytes = ControlMessageSerializer.Deserialize(ControlMessageSerializer.SerializeToUtf8Bytes(original));
        Assert.Equal(json, ControlMessageSerializer.Serialize(fromBytes));
    }

    [Fact]
    public void KnownTypes_CoverEveryWireNameOfTheProtocol()
    {
        var expected = new[]
        {
            "hello", "host.available", "request.create", "request.cancel", "request.accept", "request.reject",
            "session.endpoint", "session.connected", "session.connect_failed", "session.stats", "session.end", "ping", "pong",
            "hello.ack", "hosts.snapshot", "hosts.update", "request.created", "request.incoming", "request.result", "request.expired",
            "session.created", "session.peer_endpoint", "session.active", "session.terminate", "allowlist.updated", "error",
        };

        Assert.Equal(expected.OrderBy(x => x), ControlMessageSerializer.KnownTypes.OrderBy(x => x));
        Assert.All(expected, type => Assert.True(ControlMessageSerializer.IsKnownType(type)));
    }

    [Fact]
    public void Deserialize_ServerHelloAck_MatchesContractLiteral()
    {
        const string json = """
            {"type":"hello.ack","server_time":"2026-09-04T10:15:30.123Z","public_ip":"203.0.113.10",
             "settings":{"max_session_minutes":120,"request_timeout_seconds":60,"allowed_ports":[80,443],"log_domains":false},
             "allowlist_version":3}
            """;

        var ack = Assert.IsType<HelloAckMessage>(ControlMessageSerializer.Deserialize(json));

        Assert.Equal(T, ack.ServerTime);
        Assert.Equal(TimeSpan.Zero, ack.ServerTime.Offset);
        Assert.Equal("203.0.113.10", ack.PublicIp);
        Assert.Equal(120, ack.Settings.MaxSessionMinutes);
        Assert.Equal(60, ack.Settings.RequestTimeoutSeconds);
        Assert.Equal(new[] { 80, 443 }, ack.Settings.AllowedPorts);
        Assert.False(ack.Settings.LogDomains);
        Assert.Equal(3, ack.AllowlistVersion);
    }

    [Fact]
    public void Deserialize_SessionCreated_MatchesContractLiteral()
    {
        const string json = """
            {"type":"session.created","session_id":"dddddddd-0000-4000-8000-000000000004","role":"host",
             "secret_b64":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","expires_at":"2026-09-04T11:15:30.000Z","allowlist_version":3,
             "peer_public_ip":"198.51.100.7","same_public_ip":false,"peer":{"user_display_name":"Sara Ahmed","device_name":"SARA-LAPTOP"}}
            """;

        var created = Assert.IsType<SessionCreatedMessage>(ControlMessageSerializer.Deserialize(json));

        Assert.Equal(SessionId, created.SessionId);
        Assert.Equal("host", created.Role);
        Assert.Equal(32, Convert.FromBase64String(created.SecretB64).Length);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 11, 15, 30, TimeSpan.Zero), created.ExpiresAt);
        Assert.Equal("Sara Ahmed", created.Peer.UserDisplayName);
        Assert.False(created.SamePublicIp);
    }

    [Fact]
    public void Deserialize_HostsSnapshot_AndUpdate_ShareOneRecord_KeepingTheWireType()
    {
        const string snapshot = """{"type":"hosts.snapshot","hosts":[{"device_id":"11111111-1111-4111-8111-111111111111","user_display_name":"Omar","device_name":"OMAR-DESKTOP","reachable":null}]}""";
        const string update = """{"type":"hosts.update","hosts":[]}""";

        var s = Assert.IsType<HostsMessage>(ControlMessageSerializer.Deserialize(snapshot));
        var u = Assert.IsType<HostsMessage>(ControlMessageSerializer.Deserialize(update));

        Assert.Equal("hosts.snapshot", s.Type);
        Assert.Null(Assert.Single(s.Hosts).Reachable);
        Assert.Equal("hosts.update", u.Type);
        Assert.Empty(u.Hosts);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsUnknownControlMessage_ThatSerializesVerbatim()
    {
        const string json = """{"type":"session.relay","session_id":"x","token":"y"}""";

        var unknown = Assert.IsType<UnknownControlMessage>(ControlMessageSerializer.Deserialize(json));

        Assert.Equal("session.relay", unknown.Type);
        Assert.Equal(json, unknown.RawJson);
        Assert.Equal(json, ControlMessageSerializer.Serialize(unknown));
        Assert.Null(ControlMessageSerializer.GetRef(unknown));
    }

    [Theory]
    [InlineData("""{"ref":"c1"}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""[1,2]""")]
    [InlineData("\"hello\"")]
    public void Deserialize_MissingOrInvalidType_Throws(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ControlMessageSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => ControlMessageSerializer.Deserialize("{\"type\":\"ping\""));
    }

    [Fact]
    public void Serialize_UsesSnakeCaseWireNames_AndOmitsNulls()
    {
        Assert.Equal("""{"type":"host.available","available":false}""", ControlMessageSerializer.Serialize(new HostAvailableMessage(false, null)));
        Assert.Equal("""{"type":"ping"}""", ControlMessageSerializer.Serialize(new PingMessage()));
        Assert.Equal(
            """{"type":"request.create","ref":"c1","host_device_id":"11111111-1111-4111-8111-111111111111","duration_min":30}""",
            ControlMessageSerializer.Serialize(new RequestCreateMessage("c1", HostId, 30)));
    }

    [Fact]
    public void Serialize_WritesTimesAsUtcWithZ()
    {
        var local = new DateTimeOffset(2026, 9, 4, 13, 15, 30, 123, TimeSpan.FromHours(3));

        var json = ControlMessageSerializer.Serialize(new SessionActiveMessage(SessionId, local));

        Assert.Contains("\"expires_at\":\"2026-09-04T10:15:30.123Z\"", json);
    }

    [Fact]
    public void Deserialize_AcceptsOffsetTimes_Too()
    {
        var active = Assert.IsType<SessionActiveMessage>(ControlMessageSerializer.Deserialize(
            """{"type":"session.active","session_id":"dddddddd-0000-4000-8000-000000000004","expires_at":"2026-09-04T13:15:30.123+03:00"}"""));

        Assert.Equal(T, active.ExpiresAt);
    }

    [Fact]
    public void GetRef_ReturnsRefOnlyForCorrelatedFrames()
    {
        Assert.Equal("c1", ControlMessageSerializer.GetRef(new RequestCreateMessage("c1", HostId, 30)));
        Assert.Equal("c2", ControlMessageSerializer.GetRef(new RequestCancelMessage("c2", RequestId)));
        Assert.Equal("c3", ControlMessageSerializer.GetRef(new RequestAcceptMessage("c3", RequestId)));
        Assert.Equal("c4", ControlMessageSerializer.GetRef(new RequestRejectMessage("c4", RequestId)));
        Assert.Equal("c1", ControlMessageSerializer.GetRef(new RequestCreatedMessage("c1", RequestId, T)));
        Assert.Equal("c1", ControlMessageSerializer.GetRef(new ErrorMessage("c1", "x", "y")));
        Assert.Null(ControlMessageSerializer.GetRef(new ErrorMessage(null, "x", "y")));
        Assert.Null(ControlMessageSerializer.GetRef(new SessionActiveMessage(SessionId, T)));
        Assert.Null(ControlMessageSerializer.GetRef(new PingMessage()));
    }

    [Fact]
    public void HelloDiagnostics_RoundTripAsJsonValues()
    {
        var hello = new HelloMessage("jwt", DeviceId, "0.2.0", new Dictionary<string, object?> { ["firewall_rule_present"] = true, ["os_build"] = "22631" });

        var parsed = Assert.IsType<HelloMessage>(ControlMessageSerializer.Deserialize(ControlMessageSerializer.Serialize(hello)));

        Assert.NotNull(parsed.Diagnostics);
        Assert.True(((JsonElement)parsed.Diagnostics!["firewall_rule_present"]!).GetBoolean());
        Assert.Equal("22631", ((JsonElement)parsed.Diagnostics["os_build"]!).GetString());
        Assert.Equal("jwt", parsed.Token);
        Assert.Equal(DeviceId, parsed.DeviceId);
    }
}
