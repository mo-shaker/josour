using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Core.Tunnel;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The whole week-4 session lifecycle of <see cref="SessionCoordinator"/> against the real <see cref="ControlChannel"/> on
/// <see cref="FakeControlServer"/> (a WebSocket on 127.0.0.1) with a fake tunnel, a fake system proxy and a hand-driven clock:
/// docs/ws-protocol.md sections 3-5 and the cleanup order of docs/protocol.md section 7.
/// </summary>
public sealed class SessionCoordinatorTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private const string PeerFingerprint = "1f0a3c9b6d4e2f8a0b1c2d3e4f5061728394a5b6c7d8e9f00112233445566778";
    private const string ProbeUrl = "http://check.josour/";

    // ---------- the guest, end to end ----------

    [Fact]
    public async Task Guest_RunsTheWholeSession_FromCreatedToSessionEnd()
    {
        await using var rig = await Rig.StartAsync();

        var created = await rig.CreateSessionAsync(SessionInfo.GuestRole);

        // 1. session.created → PrepareAsync → session.endpoint with our fingerprint and candidates
        var endpoint = await rig.Connection.NextAsync<SessionEndpointMessage>();
        Assert.Equal(created, endpoint.SessionId);
        Assert.Equal(rig.Tunnels.Last.Local.CertFingerprintSha256Hex, endpoint.CertFpSha256);
        var candidate = Assert.Single(endpoint.Candidates);
        Assert.Equal("lan", candidate.Type);
        Assert.Equal("192.168.1.20", candidate.Ip);
        Assert.Equal(51234, candidate.Port);
        Assert.Equal(1, rig.Tunnels.Last.PrepareCount);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Preparing);

        // the material really is what session.created carried, secret included
        var material = rig.Tunnels.Requests[0].Material;
        Assert.Equal(TunnelRole.Guest, material.Role);
        Assert.Equal(32, material.Secret.Length);
        Assert.True(material.SamePublicIp);
        Assert.Equal("198.51.100.9", material.PeerPublicIp);

        // 2. session.peer_endpoint → ConnectAsync with the peer's pinned fingerprint
        await rig.SendPeerEndpointAsync(created);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);
        Assert.Equal(PeerFingerprint, rig.Tunnels.Last.Peer!.CertFingerprintSha256Hex);
        Assert.Equal(CandidateType.Public, Assert.Single(rig.Tunnels.Last.Peer!.Candidates).Type);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Connecting);

        // 3. session.active → the system proxy points at the local proxy, then the check page opens in the default browser
        await rig.SendActiveAsync(created);
        await rig.WaitAsync(() => rig.CheckPage.Opened.Count == 1);
        Assert.Equal(1, rig.SystemProxy.ApplyCount);
        Assert.Equal(8080, rig.SystemProxy.Port);
        Assert.True(rig.SystemProxy.IsApplied);
        Assert.Equal(ProbeUrl, rig.CheckPage.Opened[0]);
        Assert.Equal(ProbeUrl, rig.Coordinator.ProbeUrl);
        Assert.Null(rig.Coordinator.CheckPageConfirmed);

        // 4. the check page arrives: that browser really follows the system proxy
        rig.Tunnels.Last.RaiseProbeSeen();
        await rig.WaitAsync(() => rig.Coordinator.CheckPageConfirmed == true);
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);

        // 5. countdown and data used follow the tick
        rig.Tunnels.Last.Stats = new TunnelStats(1234, 5678, 2);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WaitAsync(() => rig.Coordinator.BytesUp == 1234 && rig.Coordinator.BytesDown == 5678);
        Assert.Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1), rig.Coordinator.TimeRemaining);
        Assert.True(rig.Coordinator.CanOpenCheckPage);

        // 6. Disconnect → cleanup, then session.end with this role's reason
        await rig.Coordinator.DisconnectAsync();
        var end = await rig.Connection.NextAsync<SessionEndMessage>();
        Assert.Equal(created, end.SessionId);
        Assert.Equal(SessionEndReasonNames.GuestEnded, end.Reason);
        Assert.Equal(1234, end.BytesUp);
        Assert.Equal(5678, end.BytesDown);
        Assert.Empty(end.Domains);

        // the guest never claims session.connected (docs/ws-protocol.md section 5: host only)
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionConnectedMessage);

        // no leak: tunnel ended once and disposed, the system proxy put back once, phase Ended, then back to Idle
        Assert.Equal(1, rig.Tunnels.Last.EndCount);
        Assert.Equal(TunnelEndReason.GuestEnded, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(1, rig.SystemProxy.RestoreCount);
        Assert.False(rig.SystemProxy.IsApplied);
        Assert.False(rig.Coordinator.CanOpenCheckPage);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.False(rig.Coordinator.HasLiveSession);
        rig.Coordinator.Acknowledge();
        Assert.Equal(SessionPhase.Idle, rig.Coordinator.Phase);
        Assert.Null(rig.Coordinator.CurrentSession);
    }

    [Fact]
    public async Task Guest_CanOpenTheCheckPageAgainWhileTheSessionRuns()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.RunToActiveAsync(SessionInfo.GuestRole);

        Assert.True(await rig.Coordinator.OpenCheckPageAsync());

        Assert.Equal(new[] { ProbeUrl, ProbeUrl }, rig.CheckPage.Opened);
        Assert.True(rig.Coordinator.CheckPageConfirmed);
        Assert.Equal(1, rig.SystemProxy.ApplyCount); // opening the page again does not touch the settings
        Assert.Equal(session, rig.Coordinator.CurrentSession!.SessionId);
    }

    [Fact]
    public async Task Guest_SecretNeverReachesTheLog()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();

        var secretB64 = rig.LastSecretB64!;
        var secretHex = Convert.ToHexString(Convert.FromBase64String(secretB64));
        Assert.NotEmpty(rig.Logger.Lines);
        Assert.False(rig.Logger.Contains(secretB64), "the base64 secret appeared in a log line");
        Assert.False(rig.Logger.Contains(secretHex), "the secret bytes appeared in a log line");
        Assert.False(rig.Logger.Contains("secret"), "a log line mentions the secret at all");
    }

    // ---------- the host ----------

    [Fact]
    public async Task Host_ReportsSessionConnected_AndStatsEveryThirtySeconds()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.HostRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        await rig.SendPeerEndpointAsync(session);

        // only the host turns a connected tunnel into session.connected
        var connected = await rig.Connection.NextAsync<SessionConnectedMessage>();
        Assert.Equal(session, connected.SessionId);
        Assert.Equal("lan", connected.WinnerType);
        Assert.Equal(42, connected.ConnectMs);
        Assert.Equal("1.3", connected.TlsVersion);

        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Active && rig.Clock.TimerCount >= 1);
        rig.Tunnels.Last.Stats = new TunnelStats(4096, 8192, 1);

        // nothing before 30 s …
        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(150);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionStatsMessage);

        // … one report at 30 s, the next one 30 s later
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var stats = await rig.Connection.NextAsync<SessionStatsMessage>();
        Assert.Equal(4096, stats.BytesUp);
        Assert.Equal(8192, stats.BytesDown);

        rig.Tunnels.Last.Stats = new TunnelStats(9000, 10000, 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        var second = await rig.Connection.NextAsync<SessionStatsMessage>();
        Assert.Equal(9000, second.BytesUp);

        // the host's own end reason
        await rig.Coordinator.DisconnectAsync();
        var end = await rig.Connection.NextAsync<SessionEndMessage>();
        Assert.Equal(SessionEndReasonNames.HostEnded, end.Reason);
        Assert.Equal(TunnelEndReason.HostEnded, rig.Tunnels.Last.EndReason);
    }

    [Fact]
    public async Task Host_ReportsDomains_OnlyWhenLogDomainsIsOn()
    {
        await using var off = await Rig.StartAsync();
        off.Tunnels.Domains = new[] { "example.com", "docs.example.com" };
        await off.RunToActiveAsync(SessionInfo.HostRole);
        await off.Coordinator.DisconnectAsync();
        Assert.Empty((await off.Connection.NextAsync<SessionEndMessage>()).Domains);

        await using var on = await Rig.StartAsync(logDomains: true);
        on.Tunnels.Domains = new[] { "example.com", "docs.example.com" };
        await on.RunToActiveAsync(SessionInfo.HostRole);
        await on.Coordinator.DisconnectAsync();
        Assert.Equal(new[] { "example.com", "docs.example.com" }, (await on.Connection.NextAsync<SessionEndMessage>()).Domains);
    }

    [Fact]
    public async Task Guest_NeverReportsDomains_EvenWhenLogDomainsIsOn()
    {
        await using var rig = await Rig.StartAsync(logDomains: true);
        rig.Tunnels.Domains = new[] { "example.com" };
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.Coordinator.DisconnectAsync();

        Assert.Empty((await rig.Connection.NextAsync<SessionEndMessage>()).Domains);
    }

    // ---------- the system proxy and the check page ----------

    [Fact]
    public async Task Guest_WithoutTheCheckPage_WarnsButKeepsTheSessionRunning()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);
        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.CheckPage.Opened.Count == 1);

        // the page was opened but nothing came through: that browser may ignore the system proxy, others may not
        await rig.AdvanceUntilAsync(() => rig.Coordinator.CheckPageConfirmed == false, TimeSpan.FromSeconds(1), steps: 20);

        Assert.False(rig.Coordinator.CheckPageConfirmed);
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);
        Assert.True(rig.SystemProxy.IsApplied);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task Guest_WithoutTheCheckPage_LogsWhatTheProxyCounted()
    {
        // A browser that never connected and one whose connection was turned away are opposite faults; the counters
        // are what tell them apart, so they have to reach the log.
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);
        rig.Tunnels.Last.ProxyCounters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["accepted"] = 4,
            ["rejected_by_owner"] = 4,
            ["probe_hits"] = 0,
        };
        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.CheckPage.Opened.Count == 1);

        await rig.AdvanceUntilAsync(() => rig.Coordinator.CheckPageConfirmed == false, TimeSpan.FromSeconds(1), steps: 20);

        Assert.True(rig.Logger.Contains("accepted=4"), "the counters must reach the log");
        Assert.True(rig.Logger.Contains("rejected_by_owner=4"));
        Assert.False(rig.Logger.Contains("probe_hits=0"), "zeros are noise");
    }

    [Fact]
    public async Task Guest_WithoutTheCheckPage_SaysSoWhenNothingEverConnected()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);
        rig.Tunnels.Last.ProxyCounters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["accepted"] = 0,
            ["rejected_by_owner"] = 0,
        };
        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.CheckPage.Opened.Count == 1);

        await rig.AdvanceUntilAsync(() => rig.Coordinator.CheckPageConfirmed == false, TimeSpan.FromSeconds(1), steps: 20);

        Assert.True(rig.Logger.Contains("nothing ever connected"));
    }

    [Fact]
    public async Task Guest_WithTheCheckPageSettingOff_DoesNotOpenIt_ButCanOnRequest()
    {
        await using var rig = await Rig.StartAsync();
        rig.CheckPage.OpenOnConnect = false;
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);

        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Active && rig.SystemProxy.IsApplied && rig.Coordinator.ProbeUrl is not null);

        // well past the check-page timeout: nothing was opened, so nothing is claimed either way
        await rig.AdvanceUntilAsync(() => false, TimeSpan.FromSeconds(1), steps: 15);
        Assert.Empty(rig.CheckPage.Opened);
        Assert.Null(rig.Coordinator.CheckPageConfirmed);
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);

        // the user asks for it
        rig.Tunnels.Last.RaiseProbeSeen();
        Assert.True(await rig.Coordinator.OpenCheckPageAsync());
        Assert.Single(rig.CheckPage.Opened);
        Assert.True(rig.Coordinator.CheckPageConfirmed);
    }

    [Theory]
    [InlineData("policy_locked")]
    [InlineData("authorization_denied")]
    [InlineData("apply_failed")]
    public async Task Guest_WhenTheSystemProxyCannotBeSet_EndsWithSystemProxyFailed_AndNamesTheCause(string reason)
    {
        await using var rig = await Rig.StartAsync();
        rig.SystemProxy.FailWith = reason;
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);

        await rig.SendActiveAsync(session);

        var end = await rig.Connection.NextAsync<SessionEndMessage>();
        Assert.Equal(SessionEndReasonNames.SystemProxyFailed, end.Reason);
        Assert.Equal(TunnelEndReason.SystemProxyFailed, rig.Tunnels.Last.EndReason);
        Assert.Equal(reason, rig.Coordinator.SystemProxyFailure);
        Assert.Empty(rig.CheckPage.Opened);
        Assert.False(rig.SystemProxy.IsApplied);
    }

    [Fact]
    public async Task Host_NeverTouchesTheSystemProxy()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.HostRole);
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();

        Assert.Equal(0, rig.SystemProxy.ApplyCount);
        Assert.Equal(0, rig.SystemProxy.RestoreCount);
        Assert.Empty(rig.CheckPage.Opened);
        Assert.Null(rig.Tunnels.Requests[0].RestoreSystemProxyAsync);
    }

    // ---------- the ending triggers ----------

    [Fact]
    public async Task ConnectFailure_ReportsConnectFailedWithDiagnostics_AndNoSessionEnd()
    {
        await using var rig = await Rig.StartAsync(tunnel => tunnel.ConnectResult = new TunnelConnectResult(false, null, 30000, null, "no_candidate"));
        var session = await rig.CreateSessionAsync(SessionInfo.HostRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        await rig.SendPeerEndpointAsync(session);

        var failed = await rig.Connection.NextAsync<SessionConnectFailedMessage>();
        Assert.Equal(session, failed.SessionId);
        Assert.Equal("no candidate answered", failed.Diagnostics["connect"]?.ToString());
        Assert.Equal("no_candidate", failed.Diagnostics["failure_reason"]?.ToString());

        // the server ends the session with connect_failed itself: no session.end from us
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionConnectedMessage);
        Assert.Equal(TunnelEndReason.ConnectFailed, rig.Tunnels.Last.EndReason);
    }

    [Fact]
    public async Task TunnelDeath_EndsWithTheSuggestedReason_Once()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.HostRole);

        // the guest's process vanished: the tunnel suggests guest_disconnected
        rig.Tunnels.Last.RaiseDied(TunnelEndReason.GuestDisconnected);
        var end = await rig.Connection.NextAsync<SessionEndMessage>();

        Assert.Equal(SessionEndReasonNames.GuestDisconnected, end.Reason);
        Assert.Equal(TunnelEndReason.GuestDisconnected, rig.Tunnels.Last.EndReason);

        // a second death (or a Disconnect on top of it) must not produce a second session.end
        rig.Tunnels.Last.RaiseDied(TunnelEndReason.GuestDisconnected);
        await rig.Coordinator.DisconnectAsync();
        await Task.Delay(150);
        Assert.Single(rig.Connection.Received.OfType<SessionEndMessage>());
        Assert.Equal(1, rig.Tunnels.Last.EndCount);
    }

    [Fact]
    public async Task TunnelDeath_NamesThePeer_NotThisSide()
    {
        // docs/ws-protocol.md section 9: guest_disconnected / host_disconnected are sent by the PEER of the side that
        // vanished, and this is the one moment the server cannot see by itself.
        await using var guest = await Rig.StartAsync();
        await guest.RunToActiveAsync(SessionInfo.GuestRole);
        guest.Tunnels.Last.RaiseDied(TunnelEndReason.HostDisconnected);
        Assert.Equal(SessionEndReasonNames.HostDisconnected, (await guest.Connection.NextAsync<SessionEndMessage>()).Reason);

        await using var host = await Rig.StartAsync();
        await host.RunToActiveAsync(SessionInfo.HostRole);
        host.Tunnels.Last.RaiseDied(TunnelEndReason.GuestDisconnected);
        Assert.Equal(SessionEndReasonNames.GuestDisconnected, (await host.Connection.NextAsync<SessionEndMessage>()).Reason);
    }

    [Theory]
    [InlineData(TunnelEndReason.Expired)]
    [InlineData(TunnelEndReason.ConnectFailed)]
    [InlineData(TunnelEndReason.AdminTerminated)]
    public async Task AServerVerdict_IsNeverPutInSessionEnd_EvenWhenTheTunnelSuggestsIt(TunnelEndReason verdict)
    {
        // A peer GOAWAY(expired) makes the tunnel suggest "expired"; the server answers bad_request to a client that
        // claims one of its own verdicts, so we tear down locally and wait for its session.terminate instead.
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        rig.Tunnels.Last.RaiseDied(verdict);

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.Equal(verdict, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        await Task.Delay(150);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task NoPeerEndpointWithinTheConnectBudget_ReportsConnectFailed_AndTearsDown()
    {
        // docs/ws-protocol.md section 5: the server ends a session that is not active 30 s after session.created. We do
        // not wait for its verdict while a peer that never answered leaves us in Preparing.
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        var failed = await rig.AdvanceUntilAsync(rig.Connection.NextAsync<SessionConnectFailedMessage>(), TimeSpan.FromSeconds(5), steps: 10);

        Assert.Equal(session, failed.SessionId);
        Assert.Equal("peer_endpoint_timeout", failed.Diagnostics["failure_reason"]?.ToString());
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.Equal(TunnelEndReason.ConnectFailed, rig.Tunnels.Last.EndReason);
        Assert.Equal(0, rig.Tunnels.Last.ConnectCount);
        // the server ends it with connect_failed the moment it sees that frame: no session.end on top
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task TheConnectBudget_IsDisarmedOnceTheSessionIsActive()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.AdvanceUntilAsync(() => false, TimeSpan.FromSeconds(5), steps: 10); // well past the 30 s budget

        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionConnectFailedMessage);
    }

    [Fact]
    public async Task Terminate_CleansUpLocally_WithoutSendingSessionEnd()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.Connection.SendAsync(new SessionTerminateMessage(session, SessionEndReasonNames.AdminTerminated));

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.Equal(TunnelEndReason.AdminTerminated, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(1, rig.SystemProxy.RestoreCount);
        Assert.False(rig.SystemProxy.IsApplied);
        Assert.Equal(SessionEndReasonNames.AdminTerminated, rig.Coordinator.LastEndReason);
        await Task.Delay(150);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task TwoTriggersAtOnce_ProduceExactlyOneSessionEnd()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);
        var tunnel = rig.Tunnels.Last;

        // the user presses Disconnect at the very moment the tunnel dies
        var first = rig.Coordinator.DisconnectAsync();
        var second = rig.Coordinator.EndSessionAsync(TunnelEndReason.Expired);
        tunnel.RaiseDied(TunnelEndReason.HostDisconnected);
        await Task.WhenAll(first, second);

        await rig.Connection.NextAsync<SessionEndMessage>();
        await Task.Delay(150);
        Assert.Single(rig.Connection.Received.OfType<SessionEndMessage>());
        Assert.Equal(1, tunnel.EndCount);
        Assert.Equal(TunnelEndReason.GuestEnded, tunnel.EndReason); // the first trigger decides the reason
    }

    [Fact]
    public async Task Expiry_TearsDownLocally_WithoutClaimingTheServersVerdict()
    {
        // "expired" is one of the server's own judgements (ws-protocol section 9): its timer derives from the same expires_at.
        // The client tears down at once so not a byte passes after the duration runs out, but it does not send session.end
        // because the server answers it with bad_request, and then session.terminate arrives within the difference between the two clocks.
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.AdvanceUntilAsync(() => rig.Coordinator.Phase == SessionPhase.Ended, TimeSpan.FromMinutes(1), steps: 35);

        Assert.Equal(SessionPhase.Ended, rig.Coordinator.Phase);
        Assert.Equal(TunnelEndReason.Expired, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(TimeSpan.Zero, rig.Coordinator.TimeRemaining ?? TimeSpan.Zero);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task ChannelDrop_TearsTheSessionDown_WithoutSendingSessionEnd()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        rig.Connection.Drop();

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended, TimeSpan.FromSeconds(10));
        Assert.Equal(TunnelEndReason.GuestDisconnected, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(1, rig.SystemProxy.RestoreCount);
        Assert.False(rig.SystemProxy.IsApplied);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task SleepAndResume_EndsTheLiveSessionCleanly_AndTheChannelComesBack()
    {
        // plan 8.5 (the machine sleeping): the socket does not survive the sleep, so the server has already ended the session by
        // the time Windows says "resumed". The local side must tear down — browser closed, tunnel disposed — and the
        // channel must come back on its own, with the resume signal cutting short the wait.
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        var signals = new FakeConnectivitySignals();
        using var watcher = new ConnectivityWatcher(rig.Channel, signals);

        rig.Connection.Drop();
        signals.Resume();

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended, TimeSpan.FromSeconds(10));
        Assert.Equal(TunnelEndReason.GuestDisconnected, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(1, rig.SystemProxy.RestoreCount);
        Assert.False(rig.SystemProxy.IsApplied);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);

        await rig.WaitAsync(() => rig.Channel.State == ControlChannelState.Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(1, watcher.Handled);
        Assert.False(rig.Coordinator.HasLiveSession);
    }

    [Fact]
    public async Task TokenExpiryMidSession_TearsTheSessionDownCleanly_AndTheChannelComesBack()
    {
        // plan 8.5 "the access token expiring", on the WebSocket side: the server closes the live connection with 4401. The
        // channel refreshes once and reconnects, and because the socket was gone in between the server has already ended
        // the session (guest_disconnected) — so the local side must tear down cleanly rather than keep a dead tunnel.
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.Connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired");

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended, TimeSpan.FromSeconds(10));
        Assert.Equal(TunnelEndReason.GuestDisconnected, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(1, rig.SystemProxy.RestoreCount);
        Assert.False(rig.SystemProxy.IsApplied);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage); // the socket was gone; the server knows

        // The channel itself recovered on the refreshed token, so the app is usable again without signing in.
        await rig.WaitAsync(() => rig.Channel.State == ControlChannelState.Connected, TimeSpan.FromSeconds(10));
        Assert.False(rig.Coordinator.HasLiveSession);
    }

    [Fact]
    public async Task Shutdown_EndsALiveSessionBeforeTheAppExits()
    {
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.HostRole);

        await rig.Coordinator.ShutdownAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionEndReasonNames.HostEnded, (await rig.Connection.NextAsync<SessionEndMessage>()).Reason);
        Assert.False(rig.Coordinator.HasLiveSession);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.Equal(0, rig.SystemProxy.ApplyCount);
    }

    // ---------- the allow-list ----------

    [Fact]
    public async Task Allowlist_IsNotEnforcedByDefault_AndNothingIsFetched()
    {
        // ADR-0010's default and the product's whole point: reaching sites that only serve the host's
        // country cannot depend on someone having listed them first. Nothing is fetched because there is
        // nothing to apply, and the version still travels for the host's disclosure.
        await using var rig = await Rig.StartAsync();

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        var allowlist = rig.Tunnels.Requests[0].Allowlist;
        Assert.Equal(7, allowlist.Version);
        Assert.True(allowlist.IsAllowed("anything.example", 443, new[] { 80, 443 }));
        Assert.True(allowlist.HostMatches("anything.example"));
        Assert.Empty(rig.Api.DomainsVersionCalls);
        Assert.Empty(rig.Api.DomainsCalls);
    }

    [Fact]
    public async Task Allowlist_UnenforcedStillRefusesAPortOutsideAllowedPorts()
    {
        // "Any site" is not "any port": the tunnel must not become a route to arbitrary services.
        await using var rig = await Rig.StartAsync();

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        Assert.False(rig.Tunnels.Requests[0].Allowlist.IsAllowed("anything.example", 8080, new[] { 80, 443 }));
    }


    [Fact]
    public async Task Allowlist_IsFetchedAtTheSessionsExactVersion_AndCached()
    {
        await using var rig = await Rig.StartAsync(enforceAllowlist: true);

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        var request = rig.Tunnels.Requests[0];
        Assert.Equal(7, request.Allowlist.Version);
        Assert.Equal(3, request.Allowlist.Entries.Count);
        Assert.Equal(new[] { 80, 443 }, request.AllowedPorts);
        Assert.Equal("203.0.113.7", request.OurPublicIp);

        // week 6: GET /domains?version=7, never the unversioned "current list" (docs/api.md: versions are retained).
        Assert.Equal(new[] { 7 }, rig.Api.DomainsVersionCalls);
        Assert.Empty(rig.Api.DomainsCalls);

        // a second session at the same version must not ask the server again
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();
        rig.Coordinator.Acknowledge();
        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        Assert.Equal(2, rig.Tunnels.Requests.Count);
        Assert.Equal(new[] { 7 }, rig.Api.DomainsVersionCalls);
        Assert.Equal(7, rig.Tunnels.Requests[1].Allowlist.Version);
    }

    [Fact]
    public async Task Allowlist_ANewVersion_IsFetchedAgain_NotServedFromTheCache()
    {
        await using var rig = await Rig.StartAsync(enforceAllowlist: true);

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();
        rig.Coordinator.Acknowledge();

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 8);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        Assert.Equal(new[] { 7, 8 }, rig.Api.DomainsVersionCalls);
        Assert.Equal(8, rig.Tunnels.Requests[1].Allowlist.Version);
    }

    [Fact]
    public async Task Allowlist_AVersionThatCannotBeFetched_RefusesTheSession_RatherThanGuessing()
    {
        // The tunnel enforces the list. Substituting the current list would enforce rules neither party saw, and an empty
        // list would deny everything silently. Neither is acceptable, so the session is refused with a named reason.
        await using var rig = await Rig.StartAsync(enforceAllowlist: true);
        rig.Api.DomainsVersionError = new ApiUnavailableException("the server is unreachable");

        var session = await rig.CreateSessionAsync(SessionInfo.HostRole, allowlistVersion: 9);

        var failed = await rig.Connection.NextAsync<SessionConnectFailedMessage>();
        Assert.Equal(session, failed.SessionId);
        Assert.Equal(AllowlistUnavailableException.FailureReason, failed.Diagnostics["failure_reason"]?.ToString());

        // No tunnel was ever built, so nothing could carry traffic under a list we do not have.
        Assert.Empty(rig.Tunnels.Sessions);

        // connect_failed is the server's verdict: the client tears down locally and sends no session.end.
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task Allowlist_AServerThatAnswersWithADifferentVersion_IsAlsoRefused()
    {
        // Retention says this cannot happen; if it does, the answer is not a list this session may be policed with.
        await using var rig = await Rig.StartAsync(enforceAllowlist: true);
        rig.Api.DomainsVersionOverride = 4;

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 9);

        var failed = await rig.Connection.NextAsync<SessionConnectFailedMessage>();
        Assert.Equal(AllowlistUnavailableException.FailureReason, failed.Diagnostics["failure_reason"]?.ToString());
        Assert.Empty(rig.Tunnels.Sessions);
    }

    [Fact]
    public async Task Allowlist_A404ForARetainedVersion_IsTreatedTheSameWay()
    {
        await using var rig = await Rig.StartAsync(enforceAllowlist: true);
        rig.Api.DomainsVersionError = new ApiException(System.Net.HttpStatusCode.NotFound, ApiErrorCodes.NotFound, "no such version");

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 9);

        var failed = await rig.Connection.NextAsync<SessionConnectFailedMessage>();
        Assert.Equal(AllowlistUnavailableException.FailureReason, failed.Diagnostics["failure_reason"]?.ToString());
    }

    // ---------- unauthenticated listener hits (docs/api.md reserved keys) ----------

    [Fact]
    public async Task ListenerHits_ArePostedAtSessionEnd_WithTheReservedKeysAndNothingElse()
    {
        await using var rig = await Rig.StartAsync(configure: tunnel => tunnel.Diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["listener_unauthenticated"] = 3,
            ["listener_port"] = 51234,
            ["unauthenticated_peers"] = new[] { "203.0.113.9", "198.51.100.4" },

            // The tunnel's own connect diagnostics sit in the same dictionary and must not travel with the report.
            ["winner_type"] = "public",
            ["candidate_lan_error"] = "timeout",
        });

        var session = await rig.RunToActiveAsync(SessionInfo.HostRole);
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();
        await rig.WaitAsync(() => rig.Api.DiagnosticsPosts.Count == 1);

        var posted = rig.Api.DiagnosticsPosts[0];
        Assert.Equal(session, posted.SessionId);
        Assert.Equal(SessionInfo.HostRole, posted.Role);
        Assert.Equal(3, posted.Data["listener_unauthenticated"]);
        Assert.Equal(51234, posted.Data["listener_port"]);
        Assert.Equal(new[] { "203.0.113.9", "198.51.100.4" }, Assert.IsType<string[]>(posted.Data["unauthenticated_peers"]));

        // Exactly the three documented keys: no payload, no domain, no candidate diagnostics.
        Assert.Equal(
            new[] { "listener_port", "listener_unauthenticated", "unauthenticated_peers" },
            posted.Data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ListenerHits_AreNotPostedWhenNobodyKnocked()
    {
        await using var rig = await Rig.StartAsync(); // the default fake tunnel reports only its connect diagnostics

        await rig.RunToActiveAsync(SessionInfo.HostRole);
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();

        Assert.Empty(rig.Api.DiagnosticsPosts);
    }

    [Fact]
    public async Task ListenerHits_AFailedPost_DoesNotDisturbTheEnding()
    {
        await using var rig = await Rig.StartAsync(configure: tunnel => tunnel.Diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["listener_unauthenticated"] = 1,
        });
        rig.Api.DiagnosticsError = new ApiUnavailableException("the server is unreachable");

        await rig.RunToActiveAsync(SessionInfo.HostRole);
        await rig.Coordinator.DisconnectAsync();

        // session.end still went out, the phase still settled, and the failure stayed in the log.
        await rig.Connection.NextAsync<SessionEndMessage>();
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.Single(rig.Api.DiagnosticsPosts);
    }

    // ---------- the clock (plan 8.5: "Time") ----------

    [Theory]
    [InlineData(45)]   // this machine is 45 minutes behind the server
    [InlineData(-45)]  // …and 45 minutes ahead of it
    public async Task ASkewedLocalClock_NeitherShortensNorExtendsTheSession(int serverOffsetMinutes)
    {
        // expires_at is server time; the client anchors on hello.ack.server_time and then counts monotonically, so a
        // machine whose clock is wrong still gets exactly the 30 minutes the server granted.
        await using var rig = await Rig.StartAsync(serverOffset: TimeSpan.FromMinutes(serverOffsetMinutes));

        await rig.RunToActiveAsync(SessionInfo.GuestRole);
        Assert.Equal(TimeSpan.FromMinutes(30), rig.Coordinator.TimeRemaining);

        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        await rig.WaitAsync(() => rig.Coordinator.TimeRemaining == TimeSpan.FromMinutes(20));

        // …and the session is still live: a skew this size would have expired it instantly if the countdown used the
        // local clock, or held it open for an extra 45 minutes in the other direction.
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);
        Assert.True(rig.Coordinator.HasLiveSession);
    }

    [Fact]
    public async Task ASkewedClockIsWarnedAbout_Once_PerConnect()
    {
        await using var rig = await Rig.StartAsync(serverOffset: TimeSpan.FromMinutes(45));

        await rig.WaitAsync(() => rig.Logger.Contains("clock"));

        var warning = Assert.Single(rig.Logger.Lines.Where(l => l.Contains("away from the server", StringComparison.Ordinal)));
        Assert.Contains("2700", warning, StringComparison.Ordinal); // 45 minutes, in seconds
    }

    [Fact]
    public async Task AClockWithinAMinuteOfTheServer_IsNotWorthAWarning()
    {
        await using var rig = await Rig.StartAsync(serverOffset: TimeSpan.FromSeconds(20));

        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        Assert.Equal(TimeSpan.FromMinutes(30), rig.Coordinator.TimeRemaining);
        Assert.DoesNotContain(rig.Logger.Lines, l => l.Contains("away from the server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionThatRanOutWhileTheClockWasSkewed_EndsAtTheServersMoment()
    {
        await using var rig = await Rig.StartAsync(serverOffset: TimeSpan.FromMinutes(45));
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        rig.Clock.Advance(TimeSpan.FromMinutes(29));
        await rig.WaitAsync(() => rig.Coordinator.TimeRemaining <= TimeSpan.FromMinutes(1));
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        // Locally torn down, and no session.end: "expired" is the server's verdict (docs/ws-protocol.md section 9).
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    // ---------- the rig ----------

    // ---------------------------------------------------------------- offers (ADR-0014)

    private static SessionCreatedMessage Created(Rig rig, string role) => new(
        Guid.NewGuid(),
        role,
        Convert.ToBase64String(new byte[32]),
        rig.ServerNow.AddMinutes(30),
        7,
        "198.51.100.9",
        SamePublicIp: false,
        new PeerDto(Guid.NewGuid(), Guid.NewGuid(), "Sara Ahmed", "SARA-LAPTOP"),
        Initiator: "host");

    [Fact]
    public async Task Host_OffersItsConnection_AndTheSessionFollowsTheGuestsAcceptance()
    {
        await using var rig = await Rig.StartAsync();
        var guestUserId = Guid.NewGuid();

        var offering = rig.Coordinator.OfferSessionAsync(guestUserId, 45, None);
        var create = await rig.Connection.NextAsync<OfferCreateMessage>();
        Assert.Equal((guestUserId, 45), (create.GuestUserId, create.DurationMin));
        await rig.Connection.SendAsync(new OfferCreatedMessage(create.Ref, Guid.NewGuid(), rig.ServerNow.AddSeconds(60)));

        Assert.Null(await offering);
        Assert.Equal(SessionPhase.RequestPending, rig.Coordinator.Phase);
        Assert.True(rig.Coordinator.PendingIsOffer);

        var created = Created(rig, SessionInfo.HostRole);
        await rig.Connection.SendAsync(created);
        await rig.WaitAsync(() => rig.Coordinator.CurrentSession?.SessionId == created.SessionId);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
    }

    [Fact]
    public async Task Host_AnOfferTheServerRefuses_ReturnsTheCode_AndIdle()
    {
        await using var rig = await Rig.StartAsync();

        var offering = rig.Coordinator.OfferSessionAsync(Guid.NewGuid(), 30, None);
        var create = await rig.Connection.NextAsync<OfferCreateMessage>();
        await rig.Connection.SendAsync(new ErrorMessage(create.Ref, ControlErrorCodes.GuestUnavailable, "nobody home"));

        Assert.Equal(ControlErrorCodes.GuestUnavailable, await offering);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Idle);
    }

    [Fact]
    public async Task Guest_AnOfferStartsNothingUntilThisDeviceAcceptsIt()
    {
        await using var rig = await Rig.StartAsync();
        var offerId = Guid.NewGuid();
        await rig.Connection.SendAsync(new OfferIncomingMessage(
            offerId, Guid.NewGuid(), Guid.NewGuid(), "Sara Ahmed", "SARA-LAPTOP", 30, 7, rig.ServerNow.AddSeconds(60)));
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.RequestPending);

        // A session.created before this device said yes is not one it agreed to.
        await rig.Connection.SendAsync(Created(rig, SessionInfo.GuestRole));
        await Task.Delay(100);
        Assert.Equal(SessionPhase.RequestPending, rig.Coordinator.Phase);
        Assert.Null(rig.Coordinator.CurrentSession);

        await rig.Coordinator.AcceptOfferAsync(offerId, None);
        var accept = await rig.Connection.NextAsync<OfferAcceptMessage>();
        Assert.Equal(offerId, accept.OfferId);

        var created = Created(rig, SessionInfo.GuestRole);
        await rig.Connection.SendAsync(created);
        await rig.WaitAsync(() => rig.Coordinator.CurrentSession?.SessionId == created.SessionId);
    }

    [Fact]
    public async Task Guest_AnAcceptanceTheServerRefuses_ReturnsToIdle()
    {
        await using var rig = await Rig.StartAsync();
        var offerId = Guid.NewGuid();
        await rig.Connection.SendAsync(new OfferIncomingMessage(
            offerId, Guid.NewGuid(), Guid.NewGuid(), "Sara Ahmed", "SARA-LAPTOP", 30, 7, rig.ServerNow.AddSeconds(60)));
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.RequestPending);

        await rig.Coordinator.AcceptOfferAsync(offerId, None);
        var accept = await rig.Connection.NextAsync<OfferAcceptMessage>();
        await rig.Connection.SendAsync(new ErrorMessage(accept.Ref, ControlErrorCodes.NotFound, "taken on another device"));

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Idle);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<SessionPhase> _phases = new();

        private Rig(FakeControlServer server, TestClock clock, RecordingLogger<SessionCoordinator> logger)
        {
            Server = server;
            Clock = clock;
            Logger = logger;
            Channel = new ControlChannel(
                new InMemoryAppSettingsStore(server.ServerUrl),
                new FakeAccessTokenSource(),
                new FakeDeviceInfoProvider(),
                new ControlChannelOptions
                {
                    UseSystemProxy = false,
                    OpenTimeout = TimeSpan.FromSeconds(5),
                    HelloTimeout = TimeSpan.FromSeconds(5),
                    PingInterval = TimeSpan.FromSeconds(30),
                    DeadInterval = TimeSpan.FromSeconds(60),
                    NextJitter = () => 0,
                });

            Coordinator = new SessionCoordinator(
                Channel,
                Api,
                Tunnels,
                SystemProxy,
                CheckPage,
                logger,
                clock,
                SessionCoordinatorOptions.Default with { ProbeTimeout = TimeSpan.FromSeconds(10) });
            Coordinator.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SessionCoordinator.Phase))
                {
                    lock (_phases)
                    {
                        _phases.Add(Coordinator.Phase);
                    }
                }
            };
        }

        public FakeControlServer Server { get; }

        public FakeControlConnection Connection { get; private set; } = null!;

        public ControlChannel Channel { get; }

        public TestClock Clock { get; }

        public RecordingLogger<SessionCoordinator> Logger { get; }

        public FakeApiClient Api { get; } = new();

        public FakeTunnelSessionFactory Tunnels { get; } = new();

        public FakeSystemProxyGuard SystemProxy { get; } = new();

        public FakeCheckPageOpener CheckPage { get; } = new();

        public SessionCoordinator Coordinator { get; }

        /// <summary>The <c>secret_b64</c> of the last <c>session.created</c> the rig sent.</summary>
        public string? LastSecretB64 { get; private set; }

        public SessionPhase[] Phases
        {
            get
            {
                lock (_phases)
                {
                    return _phases.ToArray();
                }
            }
        }

        /// <summary>How far this machine's clock is behind the server's; everything the rig sends is in server time.</summary>
        public TimeSpan ServerOffset { get; private set; }

        /// <summary>The time the server believes it is (what <c>expires_at</c> is expressed in).</summary>
        public DateTimeOffset ServerNow => Clock.GetUtcNow() + ServerOffset;

        /// <param name="serverOffset">Clock skew: how far <c>hello.ack.server_time</c> is ahead of this machine's clock.</param>
        public static async Task<Rig> StartAsync(
            Action<FakeTunnelSession>? configure = null,
            bool logDomains = false,
            TimeSpan? serverOffset = null,
            bool enforceAllowlist = false)
        {
            var clock = new TestClock();
            var offset = serverOffset ?? TimeSpan.Zero;
            var server = new FakeControlServer
            {
                Ack = new HelloAckMessage(
                    clock.GetUtcNow() + offset,
                    "203.0.113.7",
                    new ServerSettings(120, 60, new[] { 80, 443 }, logDomains, enforceAllowlist),
                    AllowlistVersion: 7),
            };

            var rig = new Rig(server, clock, new RecordingLogger<SessionCoordinator>()) { ServerOffset = offset };
            rig.Tunnels.Configure = configure;
            await rig.Channel.ConnectAsync("at-1", DeviceId, null, None);
            rig.Connection = await server.NextConnectionAsync();
            await rig.Connection.NextAsync<HelloMessage>();
            return rig;
        }

        /// <summary>
        /// Puts the coordinator where a real session starts - the guest asked, or the host accepted an incoming request -
        /// then sends <c>session.created</c> with a fresh 32-byte secret and returns the session id. A session.created
        /// without that agreement is ignored by design (<see cref="SessionPhaseTracker"/>).
        /// </summary>
        public async Task<Guid> CreateSessionAsync(string role, int allowlistVersion = 7)
        {
            await AgreeAsync(role);
            var sessionId = Guid.NewGuid();
            var secret = new byte[32];
            Random.Shared.NextBytes(secret);
            LastSecretB64 = Convert.ToBase64String(secret);

            await Connection.SendAsync(new SessionCreatedMessage(
                sessionId,
                role,
                LastSecretB64,
                ServerNow.AddMinutes(30),
                allowlistVersion,
                "198.51.100.9",
                SamePublicIp: true,
                new PeerDto(Guid.NewGuid(), Guid.NewGuid(), "Sara Ahmed", "SARA-LAPTOP")));

            await WaitAsync(() => Coordinator.CurrentSession?.SessionId == sessionId);
            return sessionId;
        }

        /// <summary>The guest's request (answered with <c>request.created</c>) or the host's acceptance of one.</summary>
        public async Task AgreeAsync(string role)
        {
            if (role == SessionInfo.GuestRole)
            {
                var asking = Coordinator.RequestSessionAsync(Guid.NewGuid(), 30, None);
                var create = await Connection.NextAsync<RequestCreateMessage>();
                await Connection.SendAsync(new RequestCreatedMessage(create.Ref, Guid.NewGuid(), ServerNow.AddSeconds(60)));
                Assert.Null(await asking);
                return;
            }

            var requestId = Guid.NewGuid();
            await Connection.SendAsync(new RequestIncomingMessage(
                requestId, Guid.NewGuid(), Guid.NewGuid(), "Sara Ahmed", "SARA-LAPTOP", 30, 7, ServerNow.AddSeconds(60)));
            await WaitAsync(() => Coordinator.Phase == SessionPhase.RequestPending);
            await Coordinator.AcceptRequestAsync(requestId, None);
            await Connection.NextAsync<RequestAcceptMessage>();
        }

        public Task SendPeerEndpointAsync(Guid sessionId) => Connection.SendAsync(new SessionPeerEndpointMessage(
            sessionId,
            PeerFingerprint,
            new[] { new CandidateDto("public", "198.51.100.9", 40001) }));

        public Task SendActiveAsync(Guid sessionId) => Connection.SendAsync(new SessionActiveMessage(sessionId, ServerNow.AddMinutes(30)));

        /// <summary>created → endpoint → peer_endpoint → connected → active (+ the system proxy and the check page on the guest).</summary>
        public async Task<Guid> RunToActiveAsync(string role)
        {
            var sessionId = await CreateSessionAsync(role);
            await Connection.NextAsync<SessionEndpointMessage>();
            await SendPeerEndpointAsync(sessionId);
            await WaitAsync(() => Tunnels.Last.ConnectCount == 1);
            await SendActiveAsync(sessionId);
            await WaitAsync(() => Coordinator.Phase == SessionPhase.Active && Clock.TimerCount >= 1);
            if (role == SessionInfo.GuestRole)
            {
                await WaitAsync(() => CheckPage.Opened.Count == 1);
                Tunnels.Last.RaiseProbeSeen();
                await WaitAsync(() => Coordinator.CheckPageConfirmed == true);
            }

            return sessionId;
        }

        /// <summary>Polls a condition the coordinator reaches on its own background steps.</summary>
        public async Task WaitAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"The condition was not reached. Phases: {string.Join(" → ", Phases)}");
        }

        /// <summary>Moves the hand-driven clock forward in steps until the awaited frame arrives.</summary>
        /// <summary>It advances the clock until a condition holds, not until a message arrives: needed when the correct behaviour is that no message is sent.</summary>
        public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step, int steps)
        {
            for (var i = 0; i < steps && !condition(); i++)
            {
                Clock.Advance(step);
                await Task.Delay(20);
            }
        }

        public async Task<T> AdvanceUntilAsync<T>(Task<T> awaited, TimeSpan step, int steps)
        {
            for (var i = 0; i < steps && !awaited.IsCompleted; i++)
            {
                Clock.Advance(step);
                await Task.Delay(20);
            }

            return await awaited;
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Channel.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
