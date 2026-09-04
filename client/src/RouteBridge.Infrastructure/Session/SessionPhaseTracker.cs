using RouteBridge.Core.Control;
using RouteBridge.Core.Session;

namespace RouteBridge.Infrastructure.Session;

/// <summary>
/// Maps control-channel messages to <see cref="SessionPhase"/> transitions (docs/ws-protocol.md section 5), for both roles:
/// <c>Idle → RequestPending → Preparing → Connecting → Active → Ending → Ended</c>.
/// Pure state; feed it every inbound frame via <see cref="Apply"/> and the local steps via <see cref="TrackRequest"/>,
/// <see cref="BeginEnding"/>, <see cref="MarkEnded"/>, <see cref="Reset"/>. Thread-safe; <see cref="PhaseChanged"/> is raised on the calling thread.
/// (Track B's Core SessionStateMachine may replace this in a later week; the phase names are the shared contract.)
/// </summary>
public sealed class SessionPhaseTracker
{
    private readonly object _gate = new();
    private SessionPhase _phase = SessionPhase.Idle;
    private SessionInfo? _session;
    private Guid? _pendingRequestId;
    private string? _pendingRequestRef;
    private string? _lastEndReason;

    public SessionPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public SessionInfo? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>The request id while <see cref="SessionPhase.RequestPending"/> (guest: from <c>request.created</c>; host: from <c>request.incoming</c>).</summary>
    public Guid? PendingRequestId
    {
        get
        {
            lock (_gate)
            {
                return _pendingRequestId;
            }
        }
    }

    /// <summary>Why the last request or session ended: a <c>request.result.reason</c>, an error code, or a <c>session.terminate.reason</c>.</summary>
    public string? LastEndReason
    {
        get
        {
            lock (_gate)
            {
                return _lastEndReason;
            }
        }
    }

    /// <summary>Raised after every phase change with the new phase.</summary>
    public event Action<SessionPhase>? PhaseChanged;

    /// <summary>
    /// Guest: call right before sending <c>request.create</c>. Enters <see cref="SessionPhase.RequestPending"/> ("we sent the request and
    /// await <c>request.result</c>") and remembers the <c>ref</c> so an <c>error</c> with that <c>ref</c> returns the tracker to Idle.
    /// Returns false (and does nothing) unless the tracker is Idle.
    /// </summary>
    public bool TrackRequest(string requestRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestRef);
        lock (_gate)
        {
            if (_phase != SessionPhase.Idle)
            {
                return false;
            }

            _pendingRequestRef = requestRef;
            _pendingRequestId = null;
            _lastEndReason = null;
            _phase = SessionPhase.RequestPending;
        }

        PhaseChanged?.Invoke(SessionPhase.RequestPending);
        return true;
    }

    /// <summary>Applies one inbound frame. Returns true when the phase changed.</summary>
    public bool Apply(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        SessionPhase? next = null;
        lock (_gate)
        {
            switch (message)
            {
                case RequestCreatedMessage m when _phase == SessionPhase.RequestPending && _pendingRequestRef == m.Ref:
                    _pendingRequestId = m.RequestId; // server acknowledged; phase unchanged
                    break;

                case RequestCreatedMessage m when _phase == SessionPhase.Idle && _pendingRequestRef is null:
                    // request.create was sent without TrackRequest: still track the pending request.
                    _pendingRequestId = m.RequestId;
                    _pendingRequestRef = m.Ref;
                    _lastEndReason = null;
                    next = SessionPhase.RequestPending;
                    break;

                case RequestIncomingMessage m when _phase == SessionPhase.Idle:
                    _pendingRequestId = m.RequestId;
                    _pendingRequestRef = null;
                    _lastEndReason = null;
                    next = SessionPhase.RequestPending;
                    break;

                case RequestResultMessage m when _phase == SessionPhase.RequestPending && MatchesPending(m.RequestId):
                    if (!m.Accepted)
                    {
                        _lastEndReason = m.Reason ?? "rejected";
                        ClearPending();
                        next = SessionPhase.Idle;
                    }

                    // accepted: stay RequestPending until session.created carries the session details.
                    break;

                case RequestExpiredMessage m when _phase == SessionPhase.RequestPending && MatchesPending(m.RequestId):
                    _lastEndReason = "expired";
                    ClearPending();
                    next = SessionPhase.Idle;
                    break;

                case ErrorMessage m when _phase == SessionPhase.RequestPending && m.Ref is not null && m.Ref == _pendingRequestRef:
                    _lastEndReason = m.Code;
                    ClearPending();
                    next = SessionPhase.Idle;
                    break;

                case SessionCreatedMessage m when _phase is SessionPhase.Idle or SessionPhase.RequestPending:
                    _session = SessionInfo.From(m);
                    ClearPending();
                    _lastEndReason = null;
                    next = SessionPhase.Preparing;
                    break;

                case SessionPeerEndpointMessage m when _phase == SessionPhase.Preparing && MatchesSession(m.SessionId):
                    next = SessionPhase.Connecting;
                    break;

                case SessionActiveMessage m when _phase is SessionPhase.Preparing or SessionPhase.Connecting && MatchesSession(m.SessionId):
                    _session = _session! with { ExpiresAt = m.ExpiresAt };
                    next = SessionPhase.Active;
                    break;

                case SessionTerminateMessage m when MatchesSession(m.SessionId) && _phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active or SessionPhase.Ending:
                    _lastEndReason = m.Reason;
                    if (_phase != SessionPhase.Ending)
                    {
                        next = SessionPhase.Ending;
                    }

                    break;
            }

            if (next is null || next == _phase)
            {
                return false;
            }

            _phase = next.Value;
        }

        PhaseChanged?.Invoke(next.Value);
        return true;
    }

    /// <summary>Local end (user pressed End, app closing): cleanup starts, <c>session.end</c> goes out.</summary>
    public bool BeginEnding(string? reason = null)
    {
        lock (_gate)
        {
            if (_phase is not (SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active))
            {
                return false;
            }

            _lastEndReason = reason ?? _lastEndReason;
            _phase = SessionPhase.Ending;
        }

        PhaseChanged?.Invoke(SessionPhase.Ending);
        return true;
    }

    /// <summary>Cleanup finished (protocol.md 7.6). Keeps <see cref="CurrentSession"/> for the summary until <see cref="Reset"/>.</summary>
    public bool MarkEnded()
    {
        lock (_gate)
        {
            if (_phase != SessionPhase.Ending)
            {
                return false;
            }

            _phase = SessionPhase.Ended;
        }

        PhaseChanged?.Invoke(SessionPhase.Ended);
        return true;
    }

    /// <summary>Back to Idle (from Ended, or to abandon a pending request locally). Clears the session and pending request.</summary>
    public bool Reset()
    {
        lock (_gate)
        {
            _session = null;
            ClearPending();
            if (_phase == SessionPhase.Idle)
            {
                return false;
            }

            _phase = SessionPhase.Idle;
        }

        PhaseChanged?.Invoke(SessionPhase.Idle);
        return true;
    }

    private bool MatchesPending(Guid requestId) => _pendingRequestId is null || _pendingRequestId == requestId;

    private bool MatchesSession(Guid sessionId) => _session is not null && _session.SessionId == sessionId;

    private void ClearPending()
    {
        _pendingRequestId = null;
        _pendingRequestRef = null;
    }
}
