namespace Josour.Tunnel.Mux;

/// <summary>أسباب OPEN_FAIL بقيم السلك في docs/protocol.md القسم 5.</summary>
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

/// <summary>أسباب GOAWAY بقيم السلك في docs/protocol.md القسم 5.</summary>
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

/// <summary>طلب OPEN(host, port) كما وصل من Guest. الاسم كما أرسله الطرف الآخر (غير مطبَّع بعد).</summary>
public sealed record MuxOpenRequest(string Host, int Port);

/// <summary>نتيجة OpenStreamAsync على جانب Guest: إما stream (OPEN_OK) أو سبب (OPEN_FAIL).</summary>
public sealed class MuxOpenResult
{
    private MuxOpenResult(Stream? stream, OpenFailReason? reason)
    {
        Stream = stream;
        Reason = reason;
    }

    /// <summary>الـ stream عند النجاح. يدعم الإغلاق النصفي عبر IHalfClosable.</summary>
    public Stream? Stream { get; }
    public OpenFailReason? Reason { get; }
    public bool IsOpen => Stream is not null;

    public static MuxOpenResult Ok(Stream stream) => new(stream ?? throw new ArgumentNullException(nameof(stream)), null);
    public static MuxOpenResult Fail(OpenFailReason reason) => new(null, reason);
}

/// <summary>قرار المضيف عند OPEN: OK مع stream الوجهة الذي يضخه الـ Mux ويملكه، أو FAIL بسبب.</summary>
public sealed class MuxOpenDecision
{
    private MuxOpenDecision(Stream? target, OpenFailReason? reason)
    {
        Target = target;
        Reason = reason;
    }

    /// <summary>stream الوجهة (مقبس الموقع)؛ الـ Mux يضخ في الاتجاهين ثم يتخلص منه.</summary>
    public Stream? Target { get; }
    public OpenFailReason? Reason { get; }
    public bool IsOk => Target is not null;

    public static MuxOpenDecision Ok(Stream target) => new(target ?? throw new ArgumentNullException(nameof(target)), null);
    public static MuxOpenDecision Fail(OpenFailReason reason) => new(null, reason);
}

/// <summary>إحصاءات النفق: البايتات المرسلة/المستلمة على مستوى النقل (تشمل رؤوس الإطارات) وعدد الـ streams المفتوحة.</summary>
public readonly record struct MuxStats(long BytesUp, long BytesDown, int OpenStreams);

/// <summary>إغلاق نصفي: لن يكتب هذا الطرف بعده (CLOSE في البروتوكول اليدوي، Output.Complete في Nerdbank، Shutdown(Send) على المقبس).</summary>
public interface IHalfClosable
{
    ValueTask CompleteWritingAsync(CancellationToken ct);
}

/// <summary>ما يشترك فيه الدوران فوق الـ stream المصادَق.</summary>
public interface IMuxEndpoint : IAsyncDisposable
{
    MuxStats Stats { get; }

    /// <summary>يكتمل عند موت النفق أو إغلاقه (بخطأ إن كان الموت غير متوقع).</summary>
    Task Completion { get; }

    /// <summary>سبب GOAWAY الذي أرسله الطرف الآخر إن وصل.</summary>
    GoAwayReason? RemoteGoAway { get; }

    /// <summary>PING/PONG صريح؛ يعيد زمن الذهاب والإياب.</summary>
    Task<TimeSpan> PingAsync(CancellationToken ct);

    /// <summary>GOAWAY(reason) ثم إغلاق النفق. آمن للاستدعاء أكثر من مرة.</summary>
    Task CloseAsync(GoAwayReason reason);
}

/// <summary>جانب Guest: يفتح streams إلى host:port عبر النفق.</summary>
public interface IMuxConnection : IMuxEndpoint
{
    /// <summary>OPEN ثم انتظار OPEN_OK/OPEN_FAIL. يرمي MuxClosedException إن كان النفق مغلقًا.</summary>
    Task<MuxOpenResult> OpenStreamAsync(string host, int port, CancellationToken ct);
}

/// <summary>جانب Host: يستقبل OPEN ويقرر. المعالج يُستدعى لكل OPEN بالتوازي.</summary>
public interface IMuxAcceptor : IMuxEndpoint
{
    Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
}

/// <summary>النفق مغلق أو ميت؛ لا يمكن فتح streams جديدة.</summary>
public sealed class MuxClosedException : IOException
{
    public MuxClosedException(string message, Exception? inner = null) : base(message, inner) { }
}
