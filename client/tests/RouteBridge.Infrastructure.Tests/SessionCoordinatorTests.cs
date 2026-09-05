using RouteBridge.Core.Browser;
using RouteBridge.Core.Control;
using RouteBridge.Core.Session;
using RouteBridge.Core.Tunnel;
using RouteBridge.Infrastructure.Control;
using RouteBridge.Infrastructure.Session;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// The whole week-4 session lifecycle of <see cref="SessionCoordinator"/> against the real <see cref="ControlChannel"/> on
/// <see cref="FakeControlServer"/> (a WebSocket on 127.0.0.1) with a fake tunnel, a fake work browser and a hand-driven clock:
/// docs/ws-protocol.md sections 3-5 and the cleanup order of docs/protocol.md section 7.
/// </summary>
public sealed class SessionCoordinatorTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private const string PeerFingerprint = "1f0a3c9b6d4e2f8a0b1c2d3e4f5061728394a5b6c7d8e9f00112233445566778";
    private const string ProbeUrl = "http://check.routebridge/";

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

        // 3. session.active → the work browser opens on the probe page through the local proxy
        await rig.SendActiveAsync(created);
        await rig.WaitAsync(() => rig.Browsers.Created.Count == 1);
        var options = rig.Browsers.Last.Options!;
        Assert.Equal(BrowserKind.Chrome, options.Kind);
        Assert.Equal(8080, options.ProxyPort);
        Assert.Equal(ProbeUrl, options.ProbeUrl);
        Assert.Equal(rig.Browsers.ProfileDirectory, options.ProfileDirectory);
        Assert.Equal(ProbeUrl, rig.Coordinator.ProbeUrl);

        // 4. the probe page arrives: the browser is really proxied
        rig.Tunnels.Last.RaiseProbeSeen();
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Active);

        // 5. countdown and data used follow the tick
        rig.Tunnels.Last.Stats = new TunnelStats(1234, 5678, 2);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WaitAsync(() => rig.Coordinator.BytesUp == 1234 && rig.Coordinator.BytesDown == 5678);
        Assert.Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1), rig.Coordinator.TimeRemaining);
        Assert.True(rig.Coordinator.CanReopenBrowser);

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

        // no leak: tunnel ended once and disposed, browser closed, phase Ended, then back to Idle
        Assert.Equal(1, rig.Tunnels.Last.EndCount);
        Assert.Equal(TunnelEndReason.GuestEnded, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.True(rig.Browsers.Last.CloseCount > 0);
        Assert.False(rig.Browsers.Last.IsRunning);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended);
        Assert.False(rig.Coordinator.HasLiveSession);
        rig.Coordinator.Acknowledge();
        Assert.Equal(SessionPhase.Idle, rig.Coordinator.Phase);
        Assert.Null(rig.Coordinator.CurrentSession);
    }

    [Fact]
    public async Task Guest_CanReopenTheWorkBrowserWhileTheSessionRuns()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.RunToActiveAsync(SessionInfo.GuestRole);

        var result = await rig.Coordinator.ReopenBrowserAsync(None);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(2, rig.Browsers.Created.Count); // a relaunch is a fresh launcher; the first one was closed
        Assert.True(rig.Browsers.Created[0].CloseCount > 0);
        Assert.Equal(ProbeUrl, rig.Browsers.Last.Options!.ProbeUrl);
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

    // ---------- the work browser ----------

    [Fact]
    public async Task Guest_WithoutTheProbePage_EndsWithBrowserNotProxied()
    {
        await using var rig = await Rig.StartAsync();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);
        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.Browsers.Created.Count == 1);

        // the browser is up but nothing ever reaches the probe page: it is not using our proxy
        var end = await rig.AdvanceUntilAsync(rig.Connection.NextAsync<SessionEndMessage>(), TimeSpan.FromSeconds(1), steps: 20);

        Assert.Equal(SessionEndReasonNames.BrowserNotProxied, end.Reason);
        Assert.Equal(TunnelEndReason.BrowserNotProxied, rig.Tunnels.Last.EndReason);
        Assert.Equal(BrowserLaunchFailure.ProbeTimeout, rig.Coordinator.BrowserFailure);
        Assert.Contains(ProbeUrl, rig.Coordinator.BrowserFailureDetail ?? string.Empty);
        Assert.True(rig.Browsers.Last.CloseCount > 0);
    }

    [Fact]
    public async Task Guest_WithoutAWorkBrowser_SkipsTheProbeWait_AndKeepsTheSessionRunning()
    {
        // NoWorkBrowser: nothing was launched, so nothing could bypass the proxy and browser_not_proxied would be a lie.
        // The proxy is up and the session runs on; the caller verifies it with whatever it sends through ProbeUrl.
        await using var rig = await Rig.StartAsync(browser: NoWorkBrowser.Instance);
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);

        await rig.SendActiveAsync(session);
        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Active && rig.Clock.TimerCount >= 1);

        // well past the probe timeout: still active, no browser was ever asked for, nothing to reopen
        await rig.AdvanceUntilAsync(() => false, TimeSpan.FromSeconds(1), steps: 20);
        Assert.Equal(SessionPhase.Active, rig.Coordinator.Phase);
        Assert.Empty(rig.Browsers.Created);
        Assert.Equal(ProbeUrl, rig.Coordinator.ProbeUrl);
        Assert.Null(rig.Coordinator.BrowserFailure);
        Assert.False(rig.Coordinator.CanReopenBrowser);
        Assert.Null(await rig.Coordinator.ReopenBrowserAsync(None));
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);

        // and it still ends the ordinary way
        await rig.Coordinator.DisconnectAsync();
        Assert.Equal(SessionEndReasonNames.GuestEnded, (await rig.Connection.NextAsync<SessionEndMessage>()).Reason);
    }

    [Fact]
    public async Task Guest_WithAManagedBrowser_EndsWithBrowserNotProxied_AndNamesTheCause()
    {
        await using var rig = await Rig.StartAsync();
        rig.Browsers.Results[BrowserKind.Chrome] = new BrowserLaunchResult(false, BrowserLaunchFailure.ManagedByPolicy, "HKLM:ProxyMode=system");
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);

        await rig.SendActiveAsync(session);

        var end = await rig.Connection.NextAsync<SessionEndMessage>();
        Assert.Equal(SessionEndReasonNames.BrowserNotProxied, end.Reason);
        Assert.Equal(BrowserLaunchFailure.ManagedByPolicy, rig.Coordinator.BrowserFailure);
        Assert.Equal("HKLM:ProxyMode=system", rig.Coordinator.BrowserFailureDetail);
    }

    [Fact]
    public async Task Guest_WithNoBrowserInstalled_EndsWithBrowserNotProxied_AndSaysNotFound()
    {
        await using var rig = await Rig.StartAsync();
        rig.Browsers.Installed.Clear();
        var session = await rig.CreateSessionAsync(SessionInfo.GuestRole);
        await rig.Connection.NextAsync<SessionEndpointMessage>();
        await rig.SendPeerEndpointAsync(session);
        await rig.WaitAsync(() => rig.Tunnels.Last.ConnectCount == 1);

        await rig.SendActiveAsync(session);

        Assert.Equal(SessionEndReasonNames.BrowserNotProxied, (await rig.Connection.NextAsync<SessionEndMessage>()).Reason);
        Assert.Equal(BrowserLaunchFailure.NotFound, rig.Coordinator.BrowserFailure);
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
        Assert.True(rig.Browsers.Last.CloseCount > 0);
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
        // "expired" حكم من أحكام الخادم (ws-protocol القسم 9): مؤقته مشتق من expires_at نفسه.
        // العميل يفكك فورًا حتى لا يمر بايت بعد انتهاء المدة، لكنه لا يرسل session.end
        // لأن الخادم يرد عليها bad_request، ثم يصله session.terminate خلال فارق الساعتين.
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
        Assert.True(rig.Browsers.Last.CloseCount > 0);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);
    }

    [Fact]
    public async Task SleepAndResume_EndsTheLiveSessionCleanly_AndTheChannelComesBack()
    {
        // plan 8.5 (نوم الجهاز): the socket does not survive the sleep, so the server has already ended the session by
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
        Assert.True(rig.Browsers.Last.CloseCount > 0);
        Assert.DoesNotContain(rig.Connection.Received, m => m is SessionEndMessage);

        await rig.WaitAsync(() => rig.Channel.State == ControlChannelState.Connected, TimeSpan.FromSeconds(10));
        Assert.Equal(1, watcher.Handled);
        Assert.False(rig.Coordinator.HasLiveSession);
    }

    [Fact]
    public async Task TokenExpiryMidSession_TearsTheSessionDownCleanly_AndTheChannelComesBack()
    {
        // plan 8.5 "انتهاء access token", on the WebSocket side: the server closes the live connection with 4401. The
        // channel refreshes once and reconnects, and because the socket was gone in between the server has already ended
        // the session (guest_disconnected) — so the local side must tear down cleanly rather than keep a dead tunnel.
        await using var rig = await Rig.StartAsync();
        await rig.RunToActiveAsync(SessionInfo.GuestRole);

        await rig.Connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired");

        await rig.WaitAsync(() => rig.Coordinator.Phase == SessionPhase.Ended, TimeSpan.FromSeconds(10));
        Assert.Equal(TunnelEndReason.GuestDisconnected, rig.Tunnels.Last.EndReason);
        Assert.True(rig.Tunnels.Last.Disposed);
        Assert.True(rig.Browsers.Last.CloseCount > 0);
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
        Assert.True(rig.Browsers.Created.Count == 0 || rig.Browsers.Last.CloseCount > 0);
    }

    // ---------- the allow-list ----------

    [Fact]
    public async Task Allowlist_IsFetchedOnceAndCachedByVersion()
    {
        await using var rig = await Rig.StartAsync();

        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        var request = rig.Tunnels.Requests[0];
        Assert.Equal(7, request.Allowlist.Version);
        Assert.Equal(3, request.Allowlist.Entries.Count);
        Assert.Equal(new[] { 80, 443 }, request.AllowedPorts);
        Assert.Equal("203.0.113.7", request.OurPublicIp);
        Assert.Equal(new int?[] { null }, rig.Api.DomainsCalls);

        // a second session at the same version must not ask the server again
        await rig.Coordinator.DisconnectAsync();
        await rig.Connection.NextAsync<SessionEndMessage>();
        rig.Coordinator.Acknowledge();
        await rig.CreateSessionAsync(SessionInfo.GuestRole, allowlistVersion: 7);
        await rig.Connection.NextAsync<SessionEndpointMessage>();

        Assert.Equal(2, rig.Tunnels.Requests.Count);
        Assert.Single(rig.Api.DomainsCalls);
        Assert.Equal(7, rig.Tunnels.Requests[1].Allowlist.Version);
    }

    // ---------- the clock (plan 8.5: "الوقت") ----------

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

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<SessionPhase> _phases = new();

        private Rig(FakeControlServer server, TestClock clock, RecordingLogger<SessionCoordinator> logger, IWorkBrowser? browser)
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

            Browser = new WorkBrowserSession(Browsers);
            Coordinator = new SessionCoordinator(
                Channel,
                Api,
                Tunnels,
                browser ?? Browser,
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

        public FakeWorkBrowserProvider Browsers { get; } = new();

        public WorkBrowserSession Browser { get; }

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

        /// <param name="browser">Overrides the default fake work browser (pass <see cref="NoWorkBrowser.Instance"/> for a head-less run).</param>
        /// <param name="serverOffset">Clock skew: how far <c>hello.ack.server_time</c> is ahead of this machine's clock.</param>
        public static async Task<Rig> StartAsync(
            Action<FakeTunnelSession>? configure = null,
            bool logDomains = false,
            IWorkBrowser? browser = null,
            TimeSpan? serverOffset = null)
        {
            var clock = new TestClock();
            var offset = serverOffset ?? TimeSpan.Zero;
            var server = new FakeControlServer
            {
                Ack = new HelloAckMessage(
                    clock.GetUtcNow() + offset,
                    "203.0.113.7",
                    new ServerSettings(120, 60, new[] { 80, 443 }, logDomains),
                    AllowlistVersion: 7),
            };

            var rig = new Rig(server, clock, new RecordingLogger<SessionCoordinator>(), browser) { ServerOffset = offset };
            rig.Tunnels.Configure = configure;
            await rig.Channel.ConnectAsync("at-1", DeviceId, null, None);
            rig.Connection = await server.NextConnectionAsync();
            await rig.Connection.NextAsync<HelloMessage>();
            return rig;
        }

        /// <summary>Sends <c>session.created</c> with a fresh 32-byte secret and returns the session id.</summary>
        public async Task<Guid> CreateSessionAsync(string role, int allowlistVersion = 7)
        {
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
                new PeerDto("Sara Ahmed", "SARA-LAPTOP")));

            await WaitAsync(() => Coordinator.CurrentSession?.SessionId == sessionId);
            return sessionId;
        }

        public Task SendPeerEndpointAsync(Guid sessionId) => Connection.SendAsync(new SessionPeerEndpointMessage(
            sessionId,
            PeerFingerprint,
            new[] { new CandidateDto("public", "198.51.100.9", 40001) }));

        public Task SendActiveAsync(Guid sessionId) => Connection.SendAsync(new SessionActiveMessage(sessionId, ServerNow.AddMinutes(30)));

        /// <summary>created → endpoint → peer_endpoint → connected → active (+ the probe page on the guest).</summary>
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
                await WaitAsync(() => Browsers.Created.Count == 1);
                Tunnels.Last.RaiseProbeSeen();
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
        /// <summary>يدفع الساعة حتى يتحقق شرط، لا حتى تصل رسالة: يلزم عندما يكون السلوك الصحيح ألا تُرسل رسالة.</summary>
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
            await Browser.DisposeAsync();
            await Channel.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
