namespace RouteBridge.Core.Tunnel;

/// <summary>
/// جلسة نفق واحدة بين الجهازين. المسار B يوفر التنفيذ في RouteBridge.Tunnel؛ المسار C يستهلكها من التطبيق.
/// دورة الحياة: PrepareAsync (مستمع + مرشحون) ← ConnectAsync (بعد وصول مرشحي الطرف الآخر) ← Connected ← EndAsync.
/// </summary>
public interface ITunnelSession : IAsyncDisposable
{
    Guid SessionId { get; }
    TunnelRole Role { get; }
    TunnelState State { get; }
    event Action<TunnelState>? StateChanged;

    /// <summary>يولّد الشهادة، يفتح المستمع، يطلب تعيين UPnP، ويعيد ما يُرسل في session.endpoint.</summary>
    Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct);

    /// <summary>يتصل بمرشحي الطرف الآخر بالتوازي مع الاستمرار في قبول الاتصالات الواردة. ينتهي عند أول اتصال مصادَق أو انقضاء المهلة.</summary>
    Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct);

    TunnelStats Stats { get; }

    /// <summary>النطاقات المميزة التي فُتحت عبر النفق. تُملأ على المضيف فقط.</summary>
    IReadOnlyCollection<string> DomainsSeen { get; }

    /// <summary>بيانات التشخيص لبوابة قرار Relay (المرشحون المجرَّبون، زمن وخطأ كل واحد).</summary>
    IReadOnlyDictionary<string, object?> Diagnostics { get; }

    /// <summary>ينفذ ترتيب التنظيف في docs/protocol.md القسم 7 من الخطوة 3 فصاعدًا.</summary>
    Task EndAsync(TunnelEndReason reason, CancellationToken ct);
}

/// <summary>طبقة النقل الخام. اليوم Direct (TCP)؛ لاحقًا Relay دون تغيير في المصادقة أو الـ Mux.</summary>
public interface ITunnelTransport
{
    string Name { get; }
    Task<System.IO.Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct);
}
