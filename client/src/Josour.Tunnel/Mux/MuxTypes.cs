namespace Josour.Tunnel.Mux;

/// <summary>The OPEN_FAIL reasons with the wire values from docs/protocol.md section 5.</summary>
public enum OpenFailReason : byte
{
    NotAllowed = 1,
    PrivateIp = 2,
    PortNotAllowed = 3,
    DnsFailed = 4,
    ConnectFailed = 5,
    Limit = 6,
    IpLiteral = 7,
}

/// <summary>The GOAWAY reasons with the wire values from docs/protocol.md section 5.</summary>
public enum GoAwayReason : byte
{
    SessionEnd = 1,
    ProtocolError = 2,
    Expired = 3,
}

public static class MuxWire
{
    public static string ToWire(OpenFailReason reason) => reason switch
    {
        OpenFailReason.NotAllowed => "not_allowed",
        OpenFailReason.PrivateIp => "private_ip",
        OpenFailReason.PortNotAllowed => "port_not_allowed",
        OpenFailReason.DnsFailed => "dns_failed",
        OpenFailReason.ConnectFailed => "connect_failed",
        OpenFailReason.Limit => "limit",
        OpenFailReason.IpLiteral => "ip_literal",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown OPEN_FAIL reason"),
    };

    public static string ToWire(GoAwayReason reason) => reason switch
    {
        GoAwayReason.SessionEnd => "session_end",
        GoAwayReason.ProtocolError => "protocol_error",
        GoAwayReason.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown GOAWAY reason"),
    };

    public static bool IsValid(OpenFailReason reason) => reason is >= OpenFailReason.NotAllowed and <= OpenFailReason.IpLiteral;
    public static bool IsValid(GoAwayReason reason) => reason is >= GoAwayReason.SessionEnd and <= GoAwayReason.Expired;
}

/// <summary>An OPEN(host, port) request as it arrived from the guest. The name is as the other side sent it (not normalised yet).</summary>
public sealed record MuxOpenRequest(string Host, int Port);

/// <summary>The result of OpenStreamAsync on the guest side: either a stream (OPEN_OK) or a reason (OPEN_FAIL).</summary>
public sealed class MuxOpenResult
{
    private MuxOpenResult(Stream? stream, OpenFailReason? reason)
    {
        Stream = stream;
        Reason = reason;
    }

    /// <summary>The stream on success. It supports the half-close through IHalfClosable.</summary>
    public Stream? Stream { get; }
    public OpenFailReason? Reason { get; }
    public bool IsOpen => Stream is not null;

    public static MuxOpenResult Ok(Stream stream) => new(stream ?? throw new ArgumentNullException(nameof(stream)), null);
    public static MuxOpenResult Fail(OpenFailReason reason) => new(null, reason);
}

/// <summary>The host's decision at OPEN: OK with the destination stream that the mux pumps and owns, or FAIL with a reason.</summary>
public sealed class MuxOpenDecision
{
    private MuxOpenDecision(Stream? target, OpenFailReason? reason)
    {
        Target = target;
        Reason = reason;
    }

    /// <summary>The destination stream (the site's socket); the mux pumps in both directions and then disposes of it.</summary>
    public Stream? Target { get; }
    public OpenFailReason? Reason { get; }
    public bool IsOk => Target is not null;

    public static MuxOpenDecision Ok(Stream target) => new(target ?? throw new ArgumentNullException(nameof(target)), null);
    public static MuxOpenDecision Fail(OpenFailReason reason) => new(null, reason);
}

/// <summary>The tunnel's statistics: the bytes sent/received at the transport level (frame headers included) and the number of open streams.</summary>
public readonly record struct MuxStats(long BytesUp, long BytesDown, int OpenStreams);

/// <summary>A half-close: this side will write nothing after it (CLOSE in the hand-written protocol, Output.Complete in Nerdbank, Shutdown(Send) on the socket).</summary>
public interface IHalfClosable
{
    ValueTask CompleteWritingAsync(CancellationToken ct);
}

/// <summary>What the two roles share over the authenticated stream.</summary>
public interface IMuxEndpoint : IAsyncDisposable
{
    MuxStats Stats { get; }

    /// <summary>Completes when the tunnel dies or is closed (with an error if the death was unexpected).</summary>
    Task Completion { get; }

    /// <summary>The GOAWAY reason the other side sent, if one arrived.</summary>
    GoAwayReason? RemoteGoAway { get; }

    /// <summary>An explicit PING/PONG; it returns the round-trip time.</summary>
    Task<TimeSpan> PingAsync(CancellationToken ct);

    /// <summary>GOAWAY(reason) then closing the tunnel. Safe to call more than once.</summary>
    Task CloseAsync(GoAwayReason reason);
}

/// <summary>The guest's side: it opens streams to host:port through the tunnel.</summary>
public interface IMuxConnection : IMuxEndpoint
{
    /// <summary>OPEN then waiting for OPEN_OK/OPEN_FAIL. It throws MuxClosedException if the tunnel is closed.</summary>
    Task<MuxOpenResult> OpenStreamAsync(string host, int port, CancellationToken ct);
}

/// <summary>The host's side: it receives OPEN and decides. The handler is called for every OPEN in parallel.</summary>
public interface IMuxAcceptor : IMuxEndpoint
{
    Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
}

/// <summary>The tunnel is closed or dead; no new streams can be opened.</summary>
public sealed class MuxClosedException : IOException
{
    public MuxClosedException(string message, Exception? inner = null) : base(message, inner) { }
}
