using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Transport;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests;

/// <summary>
/// A complete session over the relay (ADR-0009): with no direct candidates at all, so the only possible path is the relay.
///
/// <para>What these tests prove specifically is the point the code used to leave open: on the direct path "the listener is the
/// TLS server", and over the relay there is no listener at all — both sides connect outbound. Had the rule stayed derived from "who connected",
/// both would have waited for the other's handshake and every session would have hung. The rule adopted in docs/protocol.md section 3: the host is always the server.</para>
/// </summary>
public class RelayTunnelTests
{
    private const string GuestToken = "guest-token";
    private const string HostToken = "host-token";

    private sealed record Pair(TunnelSession Host, TunnelSession Guest, TunnelConnectResult HostResult, TunnelConnectResult GuestResult) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Guest.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static TunnelSession Build(Guid sessionId, byte[] secret, TunnelRole role, FakeRelay relay, string token, List<string> log)
    {
        var material = new SessionMaterial(sessionId, role, (byte[])secret.Clone(), DateTimeOffset.UtcNow.AddMinutes(30), false, "198.51.100.2");
        return new TunnelSession(material, new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => new StaticCandidateSource(),
            // A hostname rather than an address literal: this is RELAY_HOST's real shape, and passing "127.0.0.1" here is what hid
            // a defect that reached production — the transport was refusing the name before it opened a socket.
            Relay = new RelayEndpointInfo("localhost", relay.Port, token),
            HostEgress = role == TunnelRole.Host ? _ => new FakeEgress(log) : null,
            GuestProxy = role == TunnelRole.Guest ? _ => new FakeProxy(log) : null,
            Mux = new MuxOptions { PingInterval = TimeSpan.FromSeconds(2), DeadAfter = TimeSpan.FromSeconds(8) },
        });
    }

    private static async Task<Pair> ConnectOverRelayAsync(FakeRelay relay)
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        var log = new List<string>();
        var host = Build(sessionId, secret, TunnelRole.Host, relay, HostToken, log);
        var guest = Build(sessionId, secret, TunnelRole.Guest, relay, GuestToken, log);

        var hostLocal = await host.PrepareAsync(CancellationToken.None);
        var guestLocal = await guest.PrepareAsync(CancellationToken.None);
        var window = TimeSpan.FromSeconds(20);

        // An empty candidate array for both sides: there is no direct path in any case, so what succeeds is the relay alone.
        var hostTask = host.ConnectAsync(new PeerEndpointInfo(guestLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);
        var guestTask = guest.ConnectAsync(new PeerEndpointInfo(hostLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);
        return new Pair(host, guest, await hostTask, await guestTask);
    }

    [Fact]
    public async Task ASessionConnectsOverTheRelayWithNoDirectPathAtAll()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        Assert.True(pair.HostResult.Connected, pair.HostResult.FailureReason);
        Assert.True(pair.GuestResult.Connected, pair.GuestResult.FailureReason);
        Assert.Equal(CandidateType.Relay, pair.HostResult.WinnerType);
        Assert.Equal(CandidateType.Relay, pair.GuestResult.WinnerType);
        Assert.Equal(TunnelState.Connected, pair.Host.State);
        Assert.Equal(TunnelState.Connected, pair.Guest.State);
    }

    [Fact]
    public async Task TlsStillNegotiatesAtLeast12OverTheRelay()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        // The relay carries opaque bytes: TLS and the authentication are between the two machines exactly as on the direct path.
        Assert.Contains(pair.HostResult.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(pair.HostResult.TlsVersion, pair.GuestResult.TlsVersion);
    }

    [Fact]
    public async Task EachPartySendsItsOwnRoleAndTokenInThePreamble()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        var received = relay.Received;
        Assert.Equal(2, received.Count);
        Assert.Single(received, p => p.Role == TunnelRole.Host && p.Token == HostToken);
        Assert.Single(received, p => p.Role == TunnelRole.Guest && p.Token == GuestToken);
        Assert.Single(received.Select(p => p.SessionId).Distinct());
    }

    [Fact]
    public async Task TheRelayTokenNeverReachesTheDiagnostics()
    {
        await using var relay = new FakeRelay();
        await using var pair = await ConnectOverRelayAsync(relay);

        // The token is a bearer statement until it expires, and the diagnostics are reported to the server in session.connect_failed.
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            var rendered = string.Join("\n", session.Diagnostics.Select(kv => $"{kv.Key}={Describe(kv.Value)}"));
            Assert.DoesNotContain(HostToken, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(GuestToken, rendered, StringComparison.Ordinal);
            Assert.Contains("relay_endpoint", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARejectedRelayLeavesTheSessionUnconnectedRatherThanHanging()
    {
        await using var relay = new FakeRelay { ForcedStatus = RelayStatus.Unauthorized };
        await using var pair = await ConnectOverRelayAsync(relay);

        // With no direct path and the relay refusing, nothing is left; what matters is that it ends with a result rather than hanging until the outer timeout.
        Assert.False(pair.HostResult.Connected);
        Assert.False(pair.GuestResult.Connected);
    }

    private static string Describe(object? value) => value switch
    {
        null => "",
        IDictionary<string, object?> map => string.Join(",", map.Select(kv => $"{kv.Key}={Describe(kv.Value)}")),
        System.Collections.IEnumerable items and not string => string.Join(",", items.Cast<object?>().Select(Describe)),
        _ => value.ToString() ?? "",
    };
}
