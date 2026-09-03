namespace RouteBridge.Core.Control;

public enum ControlChannelState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// قناة التحكم مع الخادم (WSS). المسار C يوفر التنفيذ في RouteBridge.Infrastructure.
/// تعيد الاتصال بتراجع أسّي وتعيد إعلان «متاح» تلقائيًا بعد إعادة الاتصال.
/// </summary>
public interface IControlChannel : IAsyncDisposable
{
    ControlChannelState State { get; }
    event Action<ControlChannelState>? StateChanged;
    event Action<ControlMessage>? MessageReceived;

    /// <summary>يفتح الاتصال ويرسل hello وينتظر hello.ack.</summary>
    Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct);

    Task SendAsync(ControlMessage message, CancellationToken ct);

    /// <summary>يرسل رسالة تحمل ref وينتظر الرد المطابق أو error بنفس ref.</summary>
    Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct);
}
