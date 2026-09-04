namespace RouteBridge.Core.Control;

public enum ControlChannelState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>Why the channel stopped for good (no further reconnect attempts). Mirrors docs/ws-protocol.md section 7.</summary>
public enum ControlCloseReason
{
    /// <summary>Local shutdown: <see cref="IAsyncDisposable.DisposeAsync"/> (sign-out, app exit).</summary>
    Disposed,

    /// <summary>Close <c>4401</c> and a freshly refreshed token did not help either.</summary>
    Unauthorized,

    /// <summary>Close <c>4403</c>: the device was revoked or the user disabled.</summary>
    DeviceRevoked,

    /// <summary>Close <c>4409</c>: a newer connection of the same device took over.</summary>
    ReplacedByAnotherConnection,

    /// <summary>The channel cannot run at all (no server configured, no token).</summary>
    Failed,
}

/// <summary>One terminal close of the channel: the reason, the raw close code and the server's description.</summary>
public sealed record ControlChannelClosed(ControlCloseReason Reason, int? CloseCode, string? Description);

/// <summary>
/// قناة التحكم مع الخادم (WSS). المسار C يوفر التنفيذ في RouteBridge.Infrastructure.
/// تعيد الاتصال بتراجع أسّي وتعيد إعلان «متاح» تلقائيًا بعد إعادة الاتصال.
/// </summary>
public interface IControlChannel : IAsyncDisposable
{
    ControlChannelState State { get; }
    event Action<ControlChannelState>? StateChanged;
    event Action<ControlMessage>? MessageReceived;

    /// <summary>Raised once when the channel stops for a reason it will not retry (4401/4403/4409 or an unusable configuration).</summary>
    event Action<ControlChannelClosed>? Closed;

    /// <summary>يفتح الاتصال ويرسل hello وينتظر hello.ack.</summary>
    Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct);

    Task SendAsync(ControlMessage message, CancellationToken ct);

    /// <summary>
    /// يرسل رسالة تحمل ref وينتظر الرد المطابق أو error بنفس ref.
    /// <para>
    /// The real channel surfaces an <c>error</c> with the same <c>ref</c> as <see cref="ControlErrorException"/>;
    /// <c>MockControlChannel</c> (week 2) still completes the call with the <see cref="ErrorMessage"/> itself.
    /// Callers must therefore handle both until the mock is retired.
    /// </para>
    /// </summary>
    Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct);
}
