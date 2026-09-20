using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Tunnel.Tests;

/// <summary>
/// <see cref="TunnelSession"/>'s lifecycle with fake pieces (no real Egress and no real Proxy; those are in the E2E tests):
/// the preparation, the states, the tunnel dying, and the cleanup order in docs/protocol.md section 7.
/// </summary>
public class TunnelSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly string ZeroFingerprint = Convert.ToHexString(new byte[32]).ToLowerInvariant();

    private static TunnelSession NewGuest(TunnelSessionOptions? options = null, byte[]? secret = null)
        => new(new SessionMaterial(Guid.NewGuid(), TunnelRole.Guest, secret ?? TestMaterial.NewSecret(), DateTimeOffset.UtcNow.AddMinutes(30), true, "203.0.113.7"),
            options ?? new TunnelSessionOptions
            {
                BindAddress = IPAddress.Loopback,
                CandidateSource = () => new StaticCandidateSource(),
                GuestProxy = _ => new FakeProxy(),
            });

    // ---------- PrepareAsync ----------

    [Fact]
    public async Task Prepare_ReturnsFingerprintAndCandidates_AndListens()
    {
        var source = new StaticCandidateSource();
        await using var session = NewGuest(new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => source,
            GuestProxy = _ => new FakeProxy(),
            OurPublicIp = "198.51.100.9",
        });
        var states = new List<TunnelState>();
        session.StateChanged += states.Add;

        var local = await session.PrepareAsync(CancellationToken.None);

        Assert.Equal(64, local.CertFingerprintSha256Hex.Length);
        Assert.Equal(local.CertFingerprintSha256Hex, session.CertificateFingerprintHex);
        var candidate = Assert.Single(local.Candidates);
        Assert.Equal(CandidateType.Lan, candidate.Type);
        Assert.Equal(session.ListenPort, candidate.Port);
        Assert.True(session.ListenPort > 0);
        Assert.Equal(TunnelState.Listening, session.State);
        Assert.Equal(new[] { TunnelState.Listening }, states);
        Assert.False(session.CertificateDisposed);

        // The listener really does accept a connection
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, session.ListenPort).WaitAsync(Timeout);
        Assert.True(probe.Connected);

        Assert.Equal("198.51.100.9", source.LastOptions!.BackendPublicIp);
        Assert.True(source.LastOptions.SamePublicIp);
        // The mapping's lifetime = the remaining duration + 5 minutes (docs/protocol.md section 2)
        Assert.InRange(source.LastOptions.MappingLifetime, TimeSpan.FromMinutes(34), TimeSpan.FromMinutes(35.1));
        Assert.NotNull(session.GatherDiagnostics);
        Assert.Contains("gather", session.Diagnostics.Keys);
    }

    [Fact]
    public async Task Prepare_IsIdempotent()
    {
        var source = new StaticCandidateSource();
        await using var session = NewGuest(new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => source,
            GuestProxy = _ => new FakeProxy(),
        });
        var raised = 0;
        session.StateChanged += _ => Interlocked.Increment(ref raised);

        var first = await session.PrepareAsync(CancellationToken.None);
        var second = await session.PrepareAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, source.GatherCalls);
        Assert.Equal(1, raised);
    }

    // ---------- ConnectAsync ----------

    [Fact]
    public async Task Connect_BeforePrepare_Throws()
    {
        await using var session = NewGuest();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>()), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Connect_Host_WithoutEgressFactory_Throws()
    {
        await using var session = new TunnelSession(
            new SessionMaterial(Guid.NewGuid(), TunnelRole.Host, TestMaterial.NewSecret(), DateTimeOffset.UtcNow.AddMinutes(30), true, "203.0.113.7"),
            new TunnelSessionOptions { BindAddress = IPAddress.Loopback, CandidateSource = () => new StaticCandidateSource() });
        await session.PrepareAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>()), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Contains("HostEgress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_Guest_WithoutProxyFactory_Throws_UnlessProxyDisabled()
    {
        await using (var session = NewGuest(new TunnelSessionOptions { BindAddress = IPAddress.Loopback, CandidateSource = () => new StaticCandidateSource() }))
        {
            await session.PrepareAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>()), TimeSpan.FromMilliseconds(200), CancellationToken.None));
            Assert.Contains("GuestProxy", error.Message, StringComparison.Ordinal);
        }

        await using var noProxy = NewGuest(new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => new StaticCandidateSource(),
            StartGuestProxy = false,
        });
        await noProxy.PrepareAsync(CancellationToken.None);
        var result = await noProxy.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>()), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(result.Connected);
    }

    [Fact]
    public async Task Connect_Timeout_ReturnsNotConnected_WithPerCandidateDiagnostics()
    {
        await using var session = NewGuest();
        await session.PrepareAsync(CancellationToken.None);
        var dead = new CandidateEndpoint(CandidateType.Public, "127.0.0.1", Loopback.ClosedPort());

        var result = await session.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, new[] { dead }), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Null(result.WinnerType);
        Assert.Null(result.TlsVersion);
        Assert.Equal("timeout", result.FailureReason);
        Assert.Null(session.Proxy);

        var connect = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(session.Diagnostics["connect"]);
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(connect["candidates"]);
        var row = Assert.Single(rows);
        Assert.Equal("public", row["type"]);
        Assert.Equal(dead.Port, row["port"]);
        Assert.NotNull(row["error"]);
        Assert.Equal("guest", connect["role"]);
        // The state stays Connecting until EndAsync; the connector closed the listener.
        Assert.Equal(TunnelState.Connecting, session.State);
    }

    [Fact]
    public async Task Connect_Twice_Throws()
    {
        await using var session = NewGuest();
        await session.PrepareAsync(CancellationToken.None);
        var peer = new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>());
        await session.ConnectAsync(peer, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(peer, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    // ---------- EndAsync ----------

    [Fact]
    public async Task End_FromIdle_IsSafe_AndIdempotent()
    {
        var session = NewGuest();
        var states = new List<TunnelState>();
        session.StateChanged += states.Add;

        await session.EndAsync(TunnelEndReason.GuestEnded, CancellationToken.None);
        await session.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);
        await session.DisposeAsync();

        Assert.Equal(TunnelState.Ended, session.State);
        Assert.Equal(new[] { TunnelState.Ended }, states);
        Assert.Equal("GuestEnded", session.Diagnostics["end_reason"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareAsync(CancellationToken.None));
    }

    [Fact]
    public async Task End_AfterPrepare_ClosesListener_RemovesMapping_DisposesCertificate_ZeroesSecret()
    {
        var secret = TestMaterial.NewSecret();
        var source = new StaticCandidateSource(hasMapping: true);
        var session = NewGuest(new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => source,
            GuestProxy = _ => new FakeProxy(),
        }, secret);

        await session.PrepareAsync(CancellationToken.None);
        var port = session.ListenPort;
        await session.EndAsync(TunnelEndReason.Expired, CancellationToken.None);

        Assert.Equal(TunnelState.Ended, session.State);
        Assert.True(session.CertificateDisposed);
        Assert.Equal(1, source.RemoveMappingCalls);
        Assert.True(source.Disposed);
        Assert.True(session.Diagnostics.TryGetValue("upnp_mapping_removed", out var removed) && Equals(removed, true));
        Assert.All(secret, b => Assert.Equal(0, b));

        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    // ---------- A connected pair ----------

    [Fact]
    public async Task Pair_Connects_BothRoles_AndExposesProxyAndStats()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();

        Assert.True(pair.HostResult.Connected, pair.HostResult.FailureReason);
        Assert.True(pair.GuestResult.Connected, pair.GuestResult.FailureReason);
        Assert.Contains(pair.HostResult.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(TunnelState.Connected, pair.Host.State);
        Assert.Equal(TunnelState.Connected, pair.Guest.State);

        Assert.NotNull(pair.Egress.Attached);
        Assert.Null(pair.Host.Proxy);
        Assert.NotNull(pair.Guest.Proxy);
        Assert.Equal(pair.Proxy.Port, pair.Guest.Proxy!.Port);
        Assert.Equal("http://check.josour/", pair.Guest.Proxy.ProbeUrl);
        Assert.True(pair.Proxy.Started);

        // The host reports the sites' payload (the counter), and the guest reports the transport's bytes.
        Assert.Equal(11, pair.Host.Stats.BytesUp);
        Assert.Equal(22, pair.Host.Stats.BytesDown);
        Assert.Contains("example.test", pair.Host.DomainsSeen);
        Assert.Empty(pair.Guest.DomainsSeen);

        // The guest's statistics come from the transport itself: PING/PONG alone move the two counters.
        await pair.GuestMux!.PingAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.True(pair.Guest.Stats.BytesUp > 0);
        Assert.True(pair.Guest.Stats.BytesDown > 0);
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
    }

    [Fact]
    public async Task Pair_StateSequence_ListeningConnectingAuthenticatingConnectedEnded()
    {
        var pair = await TunnelSessionPair.CreateAsync();
        await pair.Guest.EndAsync(TunnelEndReason.GuestEnded, CancellationToken.None);
        await pair.Host.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);

        var expected = new[] { TunnelState.Listening, TunnelState.Connecting, TunnelState.Authenticating, TunnelState.Connected, TunnelState.Ended };
        Assert.Equal(expected, pair.HostStates);
        Assert.Equal(expected, pair.GuestStates);
        Assert.Equal("Ended", pair.Host.Diagnostics["state"]);
    }

    [Fact]
    public async Task ConnectFailed_KeepsSessionConnecting_UntilEndAsync()
    {
        var states = new List<TunnelState>();
        await using var session = NewGuest(new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            CandidateSource = () => new StaticCandidateSource(),
            StartGuestProxy = false,
        });
        session.StateChanged += states.Add;
        await session.PrepareAsync(CancellationToken.None);
        await session.ConnectAsync(new PeerEndpointInfo(ZeroFingerprint, Array.Empty<CandidateEndpoint>()), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Equal(TunnelState.Connecting, session.State);

        await session.EndAsync(TunnelEndReason.ConnectFailed, CancellationToken.None);
        Assert.Equal(new[] { TunnelState.Listening, TunnelState.Connecting, TunnelState.Ended }, states);
    }

    [Fact]
    public async Task Pair_ProbeSeen_IsForwardedOnce()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();
        var seen = 0;
        pair.Guest.ProbeSeen += () => Interlocked.Increment(ref seen);

        pair.Proxy.RaiseProbe();
        pair.Proxy.RaiseProbe();

        Assert.Equal(1, seen);
        Assert.NotNull(pair.Guest.ProbeSeenAt);
    }

    [Fact]
    public async Task Pair_KillingTransport_RaisesDied_WithDisconnectReason_OnBothSides()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();
        TunnelEndReason? hostReason = null, guestReason = null;
        pair.Host.Died += r => hostReason = r;
        pair.Guest.Died += r => guestReason = r;

        pair.HostTransport.KillAll();

        Assert.True(await Wait.UntilAsync(() => hostReason is not null && guestReason is not null, Timeout),
            $"host={hostReason} guest={guestReason} | guest connected={pair.GuestResult.Connected} ({pair.GuestResult.FailureReason}) " +
            $"state={pair.Guest.State} mux_completion={pair.Guest.Diagnostics.GetValueOrDefault("mux_completion")} " +
            $"death={pair.Guest.Diagnostics.GetValueOrDefault("death")} window={pair.Guest.Diagnostics.GetValueOrDefault("mux_window")}");
        // Each side names whoever vanished: the host sees the guest disconnect, and the guest sees the host disconnect.
        Assert.Equal(TunnelEndReason.GuestDisconnected, hostReason);
        Assert.Equal(TunnelEndReason.HostDisconnected, guestReason);
        Assert.Equal("faulted", pair.Host.Diagnostics["mux_completion"]);
        // A death is not an end: the state stays Connected until the application decides on EndAsync.
        Assert.Equal(TunnelState.Connected, pair.Host.State);
    }

    [Fact]
    public async Task Pair_CleanEnd_DoesNotLookLikeDeath_AndFollowsCleanupOrder()
    {
        var order = new List<string>();
        var certificateDisposedAtBrowserStep = true;
        var muxClosedAtBrowserStep = true;
        TunnelSessionPair? pair = null;
        pair = await TunnelSessionPair.CreateAsync(
            guestCloseBrowser: _ =>
            {
                lock (order) order.Add("browser");
                certificateDisposedAtBrowserStep = pair!.Guest.CertificateDisposed;
                muxClosedAtBrowserStep = pair.Guest.MuxClosed;
                return Task.CompletedTask;
            },
            sharedLog: order);

        var hostDied = 0;
        var guestDied = 0;
        pair.Host.Died += _ => Interlocked.Increment(ref hostDied);
        pair.Guest.Died += _ => Interlocked.Increment(ref guestDied);

        await pair.Guest.EndAsync(TunnelEndReason.GuestEnded, CancellationToken.None);
        await pair.Host.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);

        Assert.Equal(0, hostDied);
        Assert.Equal(0, guestDied);
        Assert.Equal(TunnelState.Ended, pair.Guest.State);
        Assert.Equal(TunnelState.Ended, pair.Host.State);
        Assert.True(pair.Guest.MuxClosed);
        Assert.True(pair.Host.MuxClosed);
        Assert.True(pair.Guest.CertificateDisposed);
        Assert.True(pair.Proxy.Disposed);
        Assert.True(pair.Egress.Disposed);

        // The docs/protocol.md section 7 order: stop accepting -> the browser -> GOAWAY and closing the tunnel -> stopping the proxy -> the certificate.
        var log = pair.Log;
        var stopAccepting = log.IndexOf("proxy.stop_accepting");
        var browser = log.IndexOf("browser");
        var stop = log.IndexOf("proxy.stop");
        Assert.True(stopAccepting >= 0 && browser > stopAccepting && stop > browser, string.Join(",", log));
        Assert.False(certificateDisposedAtBrowserStep);
        Assert.False(muxClosedAtBrowserStep);

        // The final statistics are kept after the end (for the session.end message)
        Assert.Equal(11, pair.Host.Stats.BytesUp);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        Assert.Contains("example.test", pair.Host.DomainsSeen);
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Pair_EndAsync_IsIdempotent_AcrossConcurrentCallers()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();
        var first = pair.Guest.EndAsync(TunnelEndReason.GuestEnded, CancellationToken.None);
        var second = pair.Guest.EndAsync(TunnelEndReason.ProtocolError, CancellationToken.None);
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        Assert.Equal("GuestEnded", pair.Guest.Diagnostics["end_reason"]);
        Assert.Equal(1, pair.Log.Count(entry => entry == "proxy.stop"));
    }

    [Fact]
    public async Task Mux_IsWiredToBothRoles_OpenReachesTheHostHandler()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();
        Assert.NotNull(pair.GuestMux);
        var open = await pair.GuestMux!.OpenStreamAsync("blocked.test", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.False(open.IsOpen);
        Assert.Equal(OpenFailReason.NotAllowed, open.Reason);
    }

    // ---------- The RTT-derived window (docs/protocol.md section 5) ----------

    /// <summary>
    /// A slow link (200 ms inside the connect_ms measurement window) => the highest band: a 4 MiB window and a limit of 64 streams,
    /// and the limit reaches the egress policy in the host's context rather than staying pinned at 256.
    /// </summary>
    [Fact]
    public async Task Pair_OnASlowLink_DerivesTheLargeWindow_AndTheMatchingStreamLimit()
    {
        await using var pair = await TunnelSessionPair.CreateAsync(connectDelay: TimeSpan.FromMilliseconds(200));

        Assert.True(pair.HostResult.Connected, pair.HostResult.FailureReason);
        Assert.InRange(pair.HostResult.ConnectMs, 151, 5000); // the measurement really is in the highest band's range
        Assert.Equal(4 * 1024 * 1024, pair.Host.Diagnostics["mux_window"]);
        Assert.Equal(64, pair.Host.Diagnostics["mux_max_streams"]);
        Assert.Equal(64, pair.EgressContext!.MaxConcurrentStreams);

        // Both sides measure roughly the same number so they land in the same band (and each side sizes its own receiving window).
        Assert.Equal(4 * 1024 * 1024, pair.Guest.Diagnostics["mux_window"]);
        Assert.Equal(64, pair.Guest.Diagnostics["mux_max_streams"]);
        Assert.Contains("> 150 ms", (string)pair.Host.Diagnostics["mux_window_reason"]!);
    }

    /// <summary>
    /// A near link (20 ms) => the lowest band: 1 MiB and 256 streams as the contract had it before the amendment.
    /// If this fails on a loaded machine, look at the connect_ms printed: a loopback handshake slower than 40 ms leaves the band.
    /// </summary>
    [Fact]
    public async Task Pair_OnANearLink_KeepsTheOneMiBWindow_And256Streams()
    {
        await using var pair = await TunnelSessionPair.CreateAsync(connectDelay: TimeSpan.FromMilliseconds(20));

        Assert.True(pair.HostResult.Connected, pair.HostResult.FailureReason);
        Assert.InRange(pair.HostResult.ConnectMs, 20, 60);
        Assert.Equal(1024 * 1024, pair.Host.Diagnostics["mux_window"]);
        Assert.Equal(256, pair.Host.Diagnostics["mux_max_streams"]);
        Assert.Equal(256, pair.EgressContext!.MaxConcurrentStreams);
    }

    /// <summary>The explicit override (the tests, the Spike tool) beats the derivation, and the limit follows the imposed window.</summary>
    [Fact]
    public async Task Pair_ExplicitWindowOverride_BeatsTheDerivation()
    {
        await using var pair = await TunnelSessionPair.CreateAsync(
            connectDelay: TimeSpan.FromMilliseconds(200),
            muxOverride: new MuxOptions { ReceiveWindow = 1024 * 1024, PingInterval = TimeSpan.FromSeconds(2), DeadAfter = TimeSpan.FromSeconds(8) });

        Assert.Equal(1024 * 1024, pair.Host.Diagnostics["mux_window"]);
        Assert.Equal(256, pair.Host.Diagnostics["mux_max_streams"]);
        Assert.Equal(256, pair.EgressContext!.MaxConcurrentStreams);
        Assert.Contains("explicit", (string)pair.Host.Diagnostics["mux_window_reason"]!);
    }

    /// <summary>Loopback with no delay gives connect_ms ≈ 0, which the contract counts as a corrupt measurement: the middle band with a written reason.</summary>
    [Fact]
    public async Task Pair_WithAnImplausibleMeasurement_FallsBackToTheMiddleTier()
    {
        await using var pair = await TunnelSessionPair.CreateAsync();

        var window = (int)pair.Host.Diagnostics["mux_window"]!;
        var streams = (int)pair.Host.Diagnostics["mux_max_streams"]!;
        var reason = (string)pair.Host.Diagnostics["mux_window_reason"]!;
        if (pair.HostResult.ConnectMs == 0)
        {
            Assert.Equal(2 * 1024 * 1024, window);
            Assert.Equal(128, streams);
            Assert.Contains("not plausible", reason);
        }
        else
        {
            // A loopback handshake may take a millisecond or two: a correct band rather than the fallback.
            Assert.Equal(1024 * 1024, window);
            Assert.Equal(256, streams);
        }
        Assert.Equal(MuxWindow.MemoryBudget, (long)window * streams);
    }
}
