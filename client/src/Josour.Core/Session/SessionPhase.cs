namespace Josour.Core.Session;

/// <summary>The session's state from the client's point of view. The server is the authority on state; this is its local reflection in the application.</summary>
public enum SessionPhase
{
    Idle,
    RequestPending,     // we sent request.create and are waiting for request.result (guest) / request.incoming arrived (host)
    Preparing,          // session.created arrived: generating the certificate, opening the listener and sending session.endpoint
    Connecting,         // session.peer_endpoint arrived: connecting to the candidates
    Active,             // session.active arrived
    Ending,             // the cleanup began
    Ended
}
