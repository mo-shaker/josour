namespace Josour.Core.Control;

/// <summary>The <c>error.code</c> values of docs/ws-protocol.md section 2.</summary>
public static class ControlErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string NotFound = "not_found";
    public const string HostUnavailable = "host_unavailable";
    public const string SessionExists = "session_exists";
    public const string RequestPending = "request_pending";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal";
}

/// <summary>The WebSocket close codes of docs/ws-protocol.md section 7.</summary>
public static class ControlCloseCodes
{
    /// <summary>No valid <c>hello</c> arrived in time (or its token was rejected).</summary>
    public const int Unauthorized = 4401;

    /// <summary>The device was revoked or the user disabled.</summary>
    public const int DeviceRevoked = 4403;

    /// <summary>A newer connection from the same device took over.</summary>
    public const int Replaced = 4409;

    /// <summary>Server restart; reconnect with exponential backoff.</summary>
    public const int ServerRestarting = 1012;
}

/// <summary>
/// The server answered a request frame with an <c>error</c> carrying the same <c>ref</c> (docs/ws-protocol.md section 2).
/// Thrown by <see cref="IControlChannel.RequestAsync"/>; <see cref="Code"/> is one of <see cref="ControlErrorCodes"/>.
/// </summary>
public sealed class ControlErrorException : Exception
{
    public ControlErrorException(string code, string message, string? correlationRef = null)
        : base(message)
    {
        Code = code;
        CorrelationRef = correlationRef;
    }

    /// <summary>The server's machine-readable <c>code</c>.</summary>
    public string Code { get; }

    /// <summary>The <c>ref</c> the error answered, when it carried one.</summary>
    public string? CorrelationRef { get; }

    public static ControlErrorException From(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ControlErrorException(error.Code, error.Message, error.Ref);
    }
}

/// <summary>
/// The channel closed and will not retry by itself: an in-flight request cannot be answered any more.
/// <see cref="Reason"/> mirrors the close code (docs/ws-protocol.md section 7).
/// </summary>
public sealed class ControlChannelClosedException : Exception
{
    public ControlChannelClosedException(ControlCloseReason reason, string message, int? closeCode = null)
        : base(message)
    {
        Reason = reason;
        CloseCode = closeCode;
    }

    public ControlCloseReason Reason { get; }

    /// <summary>The WebSocket close code, when the server sent one.</summary>
    public int? CloseCode { get; }
}
