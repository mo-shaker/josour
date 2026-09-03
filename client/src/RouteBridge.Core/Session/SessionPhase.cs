namespace RouteBridge.Core.Session;

/// <summary>حالة الجلسة من منظور العميل. الخادم مرجع الحالة؛ هذه انعكاسها المحلي في التطبيق.</summary>
public enum SessionPhase
{
    Idle,
    RequestPending,     // أرسلنا request.create وننتظر request.result (Guest) / وصلنا request.incoming (Host)
    Preparing,          // وصل session.created: توليد الشهادة وفتح المستمع وإرسال session.endpoint
    Connecting,         // وصل session.peer_endpoint: الاتصال بالمرشحين
    Active,             // وصل session.active
    Ending,             // بدأ التنظيف
    Ended
}
