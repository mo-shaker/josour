namespace Josour.Infrastructure.Session;

/// <summary>
/// How much of an incoming request's 60-second window is left, and whether the host may still answer.
///
/// <para>
/// This lives here, away from the window that draws it, because it is not a drawing concern and because
/// getting it wrong silently disables the only two buttons that matter. It did: the window compared a
/// server-issued <c>expires_at</c> against the local clock, so a host whose machine ran more than a
/// minute fast saw every incoming request arrive already expired, with Accept and Reject greyed out and
/// no explanation. The same request answered fine in the other direction, because the other machine's
/// clock was right.
/// </para>
///
/// <para>
/// Two rules follow, and the second matters more than the first.
/// </para>
/// <list type="number">
///   <item><b>Compare against server time.</b> The deadline is the server's; the offset is already known
///     from <c>hello.ack</c>, and every other countdown in the app applies it.</item>
///   <item><b>The client never declares a request dead on arrival.</b> The server is the authority on
///     expiry and says so with <c>request.expired</c>. A request that looks expired the moment it
///     arrives is far more likely to be a clock than a truth - the server would not have sent it - so
///     the countdown shows zero and the buttons stay live. Skew becomes a cosmetic problem instead of a
///     functional one, which is the difference between a wrong number and an unusable product.</item>
/// </list>
/// </summary>
public sealed class RequestCountdown
{
    private readonly DateTimeOffset _expiresAt;
    private readonly Func<DateTimeOffset> _serverNow;
    private bool _startedPositive;

    /// <param name="expiresAt">The request's deadline, in server time.</param>
    /// <param name="serverNow">The server's clock as this client best knows it - not the local clock.</param>
    public RequestCountdown(DateTimeOffset expiresAt, Func<DateTimeOffset> serverNow)
    {
        _expiresAt = expiresAt;
        _serverNow = serverNow ?? throw new ArgumentNullException(nameof(serverNow));
        _startedPositive = Remaining() > TimeSpan.Zero;
    }

    /// <summary>False when the very first reading was already at or past the deadline: the sign of a clock
    /// that disagrees with the server rather than of a request that expired in flight.</summary>
    public bool LooksSkewed => !_startedPositive;

    public int SecondsRemaining()
    {
        var remaining = Remaining();
        if (remaining > TimeSpan.Zero)
        {
            _startedPositive = true;
        }
        return (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
    }

    /// <summary>
    /// True only when the window ran out **after** having been open: a countdown that was never positive
    /// has not timed out, it has never been measured correctly, and the server will say when it really
    /// expires.
    /// </summary>
    public bool HasRunOut() => SecondsRemaining() == 0 && _startedPositive;

    private TimeSpan Remaining() => _expiresAt - _serverNow();
}
