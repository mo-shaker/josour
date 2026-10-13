using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Control.Mock;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

public sealed class SessionPhaseTrackerTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Fp = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(MockControlChannelOptions? options = null)
        {
            Channel = new MockControlChannel(options ?? MockControlChannelOptions.Fast);
            Tracker = new SessionPhaseTracker();
            Tracker.PhaseChanged += p => { lock (_phases) _phases.Add(p); };
            Channel.MessageReceived += message => Tracker.Apply(message); // subscribed BEFORE the collector: tracker state is current when a test sees a frame
            Messages = new MessageCollector(Channel);
        }

        public MockControlChannel Channel { get; }

        public SessionPhaseTracker Tracker { get; }

        public MessageCollector Messages { get; }

        private readonly List<SessionPhase> _phases = new();

        /// <summary>A safe snapshot: the phases are appended from the message dispatch loop while the test reads.</summary>
        public SessionPhase[] Phases { get { lock (_phases) return _phases.ToArray(); } }

        public async Task ConnectAsync()
        {
            await Channel.ConnectAsync("access-token", DeviceId, null, None);
            await Messages.NextAsync<HostsMessage>(m => m.Type == "hosts.snapshot");
        }

        public async ValueTask DisposeAsync()
        {
            Messages.Dispose();
            await Channel.DisposeAsync();
        }
    }

    [Fact]
    public async Task GuestPath_Idle_RequestPending_Preparing_Connecting_Active_Ending_Ended()
    {
        await using var rig = new Rig();
        await rig.ConnectAsync();
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Phase);

        Assert.True(rig.Tracker.TrackRequest("c1"));
        var created = Assert.IsType<RequestCreatedMessage>(await rig.Channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 30), Timeout, None));

        // RequestAsync completes from the ref waiter, which runs BEFORE the dispatch loop hands the same frame to the
        // subscribers: waiting for the collector to see it is what makes "the tracker has applied it" true rather than likely.
        await rig.Messages.NextAsync<RequestCreatedMessage>();
        Assert.Equal(SessionPhase.RequestPending, rig.Tracker.Phase);
        Assert.Equal(created.RequestId, rig.Tracker.PendingRequestId);

        var session = await rig.Messages.NextAsync<SessionCreatedMessage>();
        Assert.Equal(SessionPhase.Preparing, rig.Tracker.Phase);
        Assert.Null(rig.Tracker.PendingRequestId);
        var info = rig.Tracker.CurrentSession!;
        Assert.Equal(session.SessionId, info.SessionId);
        Assert.False(info.IsHost);
        Assert.Equal("Omar Hassan", info.PeerUserDisplayName);
        Assert.Equal("OMAR-DESKTOP", info.PeerDeviceName);
        Assert.Equal(session.ExpiresAt, info.ExpiresAt);

        await rig.Channel.SendAsync(new SessionEndpointMessage(session.SessionId, Fp, new[] { new CandidateDto("lan", "192.168.1.2", 45000) }), None);
        await rig.Messages.NextAsync<SessionPeerEndpointMessage>();
        // The simulated channel advances to session.active of its own accord, so asserting the instantaneous phase here is a race.
        // Connecting being present in the log proves the transition happened, and its ordering is confirmed in the sequence assertion below.
        Assert.Contains(SessionPhase.Connecting, rig.Phases);

        await rig.Messages.NextAsync<SessionActiveMessage>();
        Assert.Equal(SessionPhase.Active, rig.Tracker.Phase);

        Assert.True(rig.Tracker.BeginEnding("guest_ended"));
        await rig.Channel.SendAsync(new SessionEndMessage(session.SessionId, "guest_ended", 1, 2, Array.Empty<string>()), None);
        await rig.Messages.NextAsync<SessionTerminateMessage>();
        Assert.Equal(SessionPhase.Ending, rig.Tracker.Phase);

        Assert.True(rig.Tracker.MarkEnded());
        Assert.Equal(SessionPhase.Ended, rig.Tracker.Phase);
        Assert.Equal("guest_ended", rig.Tracker.LastEndReason);
        Assert.NotNull(rig.Tracker.CurrentSession); // kept for the summary until Reset

        Assert.Equal(
            new[] { SessionPhase.RequestPending, SessionPhase.Preparing, SessionPhase.Connecting, SessionPhase.Active, SessionPhase.Ending, SessionPhase.Ended },
            rig.Phases);

        Assert.True(rig.Tracker.Reset());
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Phase);
        Assert.Null(rig.Tracker.CurrentSession);
    }

    [Fact]
    public async Task HostPath_RequestIncoming_Accept_Endpoint_Connected_Terminate()
    {
        await using var rig = new Rig();
        await rig.ConnectAsync();

        var requestId = rig.Channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 45)!.Value;
        await rig.Messages.NextAsync<RequestIncomingMessage>();
        Assert.Equal(SessionPhase.RequestPending, rig.Tracker.Phase);
        Assert.Equal(requestId, rig.Tracker.PendingRequestId);

        Assert.True(rig.Tracker.MarkAgreed(requestId)); // what SessionCoordinator.AcceptRequestAsync does before sending
        await rig.Channel.SendAsync(new RequestAcceptMessage("c7", requestId), None);
        var session = await rig.Messages.NextAsync<SessionCreatedMessage>();
        Assert.Equal(SessionPhase.Preparing, rig.Tracker.Phase);
        Assert.True(rig.Tracker.CurrentSession!.IsHost);
        Assert.Equal("Sara Ahmed", rig.Tracker.CurrentSession.PeerUserDisplayName);

        await rig.Channel.SendAsync(new SessionEndpointMessage(session.SessionId, Fp, new[] { new CandidateDto("public", "203.0.113.55", 45000) }), None);
        await rig.Messages.NextAsync<SessionPeerEndpointMessage>();
        // The simulated channel advances to session.active of its own accord, so asserting the instantaneous phase here is a race.
        // Connecting being present in the log proves the transition happened, and its ordering is confirmed in the sequence assertion below.
        Assert.Contains(SessionPhase.Connecting, rig.Phases);

        await rig.Channel.SendAsync(new SessionConnectedMessage(session.SessionId, "public", 120, "1.3"), None);
        await rig.Messages.NextAsync<SessionActiveMessage>();
        Assert.Equal(SessionPhase.Active, rig.Tracker.Phase);

        // Remote end (server-side terminate) without a local BeginEnding: the tracker enters Ending on its own.
        await rig.Channel.SendAsync(new SessionEndMessage(session.SessionId, "host_ended", 0, 0, Array.Empty<string>()), None);
        await rig.Messages.NextAsync<SessionTerminateMessage>();
        Assert.Equal(SessionPhase.Ending, rig.Tracker.Phase);
        Assert.Equal("host_ended", rig.Tracker.LastEndReason);

        Assert.True(rig.Tracker.MarkEnded());
        Assert.Equal(
            new[] { SessionPhase.RequestPending, SessionPhase.Preparing, SessionPhase.Connecting, SessionPhase.Active, SessionPhase.Ending, SessionPhase.Ended },
            rig.Phases);
    }

    [Fact]
    public async Task RejectedRequest_ReturnsToIdle_WithReason()
    {
        await using var rig = new Rig(MockControlChannelOptions.Fast with { AutoAcceptGuestRequests = false });
        await rig.ConnectAsync();

        rig.Tracker.TrackRequest("c1");
        await rig.Channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 30), Timeout, None);
        await rig.Messages.NextAsync<RequestResultMessage>(m => !m.Accepted);

        Assert.Equal(SessionPhase.Idle, rig.Tracker.Phase);
        Assert.Equal("rejected", rig.Tracker.LastEndReason);
        Assert.Null(rig.Tracker.PendingRequestId);
        Assert.Equal(new[] { SessionPhase.RequestPending, SessionPhase.Idle }, rig.Phases);
    }

    [Fact]
    public async Task ErrorForTrackedRef_ReturnsToIdle_WithErrorCode()
    {
        await using var rig = new Rig();
        await rig.ConnectAsync();

        rig.Tracker.TrackRequest("c1");
        var reply = await rig.Channel.RequestAsync(new RequestCreateMessage("c1", Guid.NewGuid(), 30), Timeout, None);

        Assert.IsType<ErrorMessage>(reply);
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Phase);
        Assert.Equal("host_unavailable", rig.Tracker.LastEndReason);
        Assert.Equal(new[] { SessionPhase.RequestPending, SessionPhase.Idle }, rig.Phases);
    }

    [Fact]
    public async Task IncomingRequestExpiry_ReturnsHostToIdle()
    {
        await using var rig = new Rig(MockControlChannelOptions.Fast with { Settings = MockControlChannelOptions.Fast.Settings with { RequestTimeoutSeconds = 1 } });
        await rig.ConnectAsync();

        rig.Channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 30);
        await rig.Messages.NextAsync<RequestIncomingMessage>();
        Assert.Equal(SessionPhase.RequestPending, rig.Tracker.Phase);

        await rig.Messages.NextAsync<RequestExpiredMessage>(timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(SessionPhase.Idle, rig.Tracker.Phase);
        Assert.Equal("expired", rig.Tracker.LastEndReason);
    }

    [Fact]
    public void UnrelatedFrames_AndForeignSessions_DoNotChangeThePhase()
    {
        var tracker = new SessionPhaseTracker();
        var created = new SessionCreatedMessage(Guid.NewGuid(), "guest", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(30), 1, "203.0.113.10", false, new PeerDto(Guid.NewGuid(), Guid.NewGuid(), "Omar", "OMAR-DESKTOP"));

        Assert.False(tracker.Apply(new HostsMessage("hosts.snapshot", Array.Empty<HostInfoDto>())));
        Assert.False(tracker.Apply(new PingMessage()));
        Assert.True(tracker.TrackRequest("r1"));
        Assert.True(tracker.Apply(created));
        Assert.False(tracker.Apply(new SessionPeerEndpointMessage(Guid.NewGuid(), Fp, Array.Empty<CandidateDto>())));
        Assert.False(tracker.Apply(new SessionTerminateMessage(Guid.NewGuid(), "expired")));
        Assert.Equal(SessionPhase.Preparing, tracker.Phase);

        Assert.True(tracker.Apply(new SessionActiveMessage(created.SessionId, created.ExpiresAt.AddMinutes(1)))); // active may skip Connecting (host role timing)
        Assert.Equal(SessionPhase.Active, tracker.Phase);
        Assert.Equal(created.ExpiresAt.AddMinutes(1), tracker.CurrentSession!.ExpiresAt);

        Assert.False(tracker.MarkEnded()); // only valid from Ending
        Assert.True(tracker.Apply(new SessionTerminateMessage(created.SessionId, "admin_terminated")));
        Assert.Equal(SessionPhase.Ending, tracker.Phase);
        Assert.False(tracker.Apply(new SessionTerminateMessage(created.SessionId, "admin_terminated"))); // duplicate: no change
        Assert.True(tracker.MarkEnded());
        Assert.Equal("admin_terminated", tracker.LastEndReason);
    }

    [Fact]
    public void ASessionNobodyHereAgreedTo_IsIgnored()
    {
        // The client used to accept session.created from Idle, i.e. a session the server started on its own. That is the
        // one thing a host's consent exists to prevent, so the frame is ignored unless this side asked, offered, or
        // answered yes.
        var tracker = new SessionPhaseTracker();
        var created = new SessionCreatedMessage(Guid.NewGuid(), "host", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(30), 1, "203.0.113.10", false, new PeerDto(Guid.NewGuid(), Guid.NewGuid(), "Omar", "OMAR-DESKTOP"));

        Assert.False(tracker.Apply(created));
        Assert.Equal(SessionPhase.Idle, tracker.Phase);

        var requestId = Guid.NewGuid();
        Assert.True(tracker.Apply(new RequestIncomingMessage(requestId, Guid.NewGuid(), Guid.NewGuid(), "Omar", "OMAR-DESKTOP", 30, 1, DateTimeOffset.UtcNow.AddSeconds(60))));
        Assert.False(tracker.Apply(created)); // still unanswered
        Assert.Equal(SessionPhase.RequestPending, tracker.Phase);

        Assert.False(tracker.MarkAgreed(Guid.NewGuid())); // not the pending one
        Assert.True(tracker.MarkAgreed(requestId));
        Assert.True(tracker.Apply(created));
        Assert.Equal(SessionPhase.Preparing, tracker.Phase);
    }

    [Fact]
    public void HostOffer_Created_Rejected_ReturnsToIdle()
    {
        var tracker = new SessionPhaseTracker();
        var offerId = Guid.NewGuid();

        Assert.True(tracker.TrackOffer("o1"));
        Assert.True(tracker.PendingIsOffer);
        Assert.False(tracker.Apply(new OfferCreatedMessage("o1", offerId, DateTimeOffset.UtcNow.AddSeconds(60))));
        Assert.Equal(offerId, tracker.PendingRequestId);

        Assert.False(tracker.Apply(new OfferResultMessage(Guid.NewGuid(), false, "rejected", null))); // someone else's
        Assert.True(tracker.Apply(new OfferResultMessage(offerId, false, "rejected", null)));
        Assert.Equal(SessionPhase.Idle, tracker.Phase);
        Assert.Equal("rejected", tracker.LastEndReason);
    }

    [Fact]
    public void GuestOffer_Incoming_Accepted_StartsTheSession_ClosedElsewhere_ReturnsToIdle()
    {
        var created = new SessionCreatedMessage(Guid.NewGuid(), "guest", Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(30), 1, "203.0.113.10", false, new PeerDto(Guid.NewGuid(), Guid.NewGuid(), "Omar", "OMAR-DESKTOP"), Initiator: "host");
        OfferIncomingMessage Offer(Guid id) => new(id, Guid.NewGuid(), Guid.NewGuid(), "Omar", "OMAR-DESKTOP", 30, 1, DateTimeOffset.UtcNow.AddSeconds(60));

        var accepted = new SessionPhaseTracker();
        var offerId = Guid.NewGuid();
        Assert.True(accepted.Apply(Offer(offerId)));
        Assert.True(accepted.PendingIsOffer);
        Assert.True(accepted.MarkAgreed(offerId));
        Assert.True(accepted.Apply(created));
        Assert.Equal(SessionPhase.Preparing, accepted.Phase);

        var elsewhere = new SessionPhaseTracker();
        var other = Guid.NewGuid();
        Assert.True(elsewhere.Apply(Offer(other)));
        Assert.True(elsewhere.Apply(new OfferClosedMessage(other, "accepted_elsewhere")));
        Assert.Equal(SessionPhase.Idle, elsewhere.Phase);
        Assert.Equal("accepted_elsewhere", elsewhere.LastEndReason);
    }

    [Fact]
    public void TrackRequest_OnlyFromIdle()
    {
        var tracker = new SessionPhaseTracker();

        Assert.True(tracker.TrackRequest("c1"));
        Assert.False(tracker.TrackRequest("c2"));
        Assert.Equal(SessionPhase.RequestPending, tracker.Phase);

        Assert.False(tracker.Apply(new ErrorMessage("c2", "bad_request", "other ref"))); // not ours
        Assert.True(tracker.Apply(new ErrorMessage("c1", "session_exists", "busy")));
        Assert.Equal(SessionPhase.Idle, tracker.Phase);
        Assert.Equal("session_exists", tracker.LastEndReason);
    }
}
