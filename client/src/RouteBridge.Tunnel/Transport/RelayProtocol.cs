using System.Buffers.Binary;
using System.Text;
using RouteBridge.Core.Tunnel;

namespace RouteBridge.Tunnel.Transport;

/// <summary>حالة مصافحة الـ Relay كما يعيدها الخادم في بايت واحد.</summary>
public enum RelayStatus : byte
{
    /// <summary>الطرفان متزاوجان؛ ما بعد هذا البايت بايتات معتمة (TLS بين الجهازين).</summary>
    Paired = 0,
    Unauthorized = 1,
    UnknownSession = 2,
    /// <summary>لم يصل الطرف الآخر خلال مهلة الاقتران عند الـ Relay.</summary>
    NoPeer = 3,
    /// <summary>الدور نفسه مسجَّل لهذه الجلسة بالفعل.</summary>
    Busy = 4,
    ProtocolError = 5,
    Internal = 6,
}

/// <summary>مصافحة الـ Relay فشلت بحالة صريحة.</summary>
public sealed class RelayRejectedException : IOException
{
    public RelayRejectedException(RelayStatus status)
        : base($"relay rejected the session: {RelayProtocol.ToWire(status)}")
        => Status = status;

    public RelayStatus Status { get; }
}

/// <summary>مقدمة الـ Relay غير صالحة (سحر أو إصدار أو أطوال).</summary>
public sealed class RelayProtocolException : IOException
{
    public RelayProtocolException(string message) : base(message) { }
}

/// <summary>ما يقرأه الـ Relay من الطرف قبل الاقتران.</summary>
public sealed record RelayPreamble(Guid SessionId, TunnelRole Role, string Token);

/// <summary>
/// عقد السلك الصغير بين العميل والـ Relay (ADR-0003 البند 4 و5). الغرض أن يصير Relay «قلب إعداد» لا إعادة تصميم:
/// النقل وحده يتغير، وTLS والمصادقة (docs/protocol.md القسمان 3 و4) يبقيان بين الجهازين فوق بايتات الـ Relay المعتمة.
///
/// المقدمة (العميل ← Relay)، 24 بايت ثابتة + التوكن:
/// <code>
///   4B  magic = "RBRL"
///   u8  version = 1
///   u8  role    (0 = guest, 1 = host)   ← الـ Relay يزاوج دورين مختلفين لنفس session_id
///   16B session_id (بايتات UUID big-endian، نفس ترتيب AUTH1)
///   u16 token_length (big-endian، 1..1024)
///   NB  token (موقَّع من الخادم الخلفي، UTF-8)
/// </code>
/// الرد (Relay ← العميل)، بايتان: <c>u8 version = 1 | u8 status</c> (<see cref="RelayStatus"/>).
///
/// WEEK 5/6: جانب الخادم (خدمة الـ Relay التي تقرأ المقدمة، تتحقق من التوكن، تزاوج الطرفين وتضخ البايتات) لا يُبنى
/// إلا إذا أغلقت بوابة القرار في ADR-0003 (أقل من 85% نجاحًا للمباشر خلال 10 ثوانٍ على 10 أزواج حقيقية).
/// هنا جانب العميل فقط، مع <see cref="ReadPreambleAsync"/> و<see cref="BuildReply"/> ليختبره Relay وهمي داخل العملية.
/// </summary>
public static class RelayProtocol
{
    public const byte Version = 1;
    public const int MagicLength = 4;
    public const int SessionIdLength = 16;
    public const int TokenLengthOffset = MagicLength + 1 + 1 + SessionIdLength;
    /// <summary>طول الجزء الثابت من المقدمة قبل التوكن.</summary>
    public const int FixedPreambleLength = TokenLengthOffset + 2; // 24
    public const int MaxTokenLength = 1024;
    public const int ReplyLength = 2;
    public const byte GuestRole = 0;
    public const byte HostRole = 1;

    private static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(5);

    public static ReadOnlySpan<byte> Magic => "RBRL"u8;

    public static string ToWire(RelayStatus status) => status switch
    {
        RelayStatus.Paired => "paired",
        RelayStatus.Unauthorized => "unauthorized",
        RelayStatus.UnknownSession => "unknown_session",
        RelayStatus.NoPeer => "no_peer",
        RelayStatus.Busy => "busy",
        RelayStatus.ProtocolError => "protocol_error",
        RelayStatus.Internal => "internal",
        _ => "unknown",
    };

    public static byte RoleByte(TunnelRole role) => role == TunnelRole.Host ? HostRole : GuestRole;

    // ---------- المقدمة ----------

    public static byte[] BuildPreamble(Guid sessionId, TunnelRole role, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > MaxTokenLength)
            throw new ArgumentOutOfRangeException(nameof(token), $"token must not exceed {MaxTokenLength} bytes when UTF-8 encoded");

        var buffer = new byte[FixedPreambleLength + tokenBytes.Length];
        Magic.CopyTo(buffer);
        buffer[MagicLength] = Version;
        buffer[MagicLength + 1] = RoleByte(role);
        sessionId.ToByteArray(bigEndian: true).CopyTo(buffer, MagicLength + 2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(TokenLengthOffset), (ushort)tokenBytes.Length);
        tokenBytes.CopyTo(buffer, FixedPreambleLength);
        return buffer;
    }

    /// <summary>يحلل مقدمة كاملة في المخزن. false مع سبب وصفي عند أي خلل (بلا أسرار في النص).</summary>
    public static bool TryParsePreamble(ReadOnlySpan<byte> buffer, out RelayPreamble? preamble, out string? error)
    {
        preamble = null;
        error = null;
        if (buffer.Length < FixedPreambleLength) { error = "preamble is shorter than the fixed header"; return false; }
        if (!buffer[..MagicLength].SequenceEqual(Magic)) { error = "bad magic"; return false; }
        if (buffer[MagicLength] != Version) { error = $"unsupported version {buffer[MagicLength]}"; return false; }

        var roleByte = buffer[MagicLength + 1];
        if (roleByte is not (GuestRole or HostRole)) { error = $"bad role {roleByte}"; return false; }
        var role = roleByte == HostRole ? TunnelRole.Host : TunnelRole.Guest;

        var sessionId = new Guid(buffer.Slice(MagicLength + 2, SessionIdLength), bigEndian: true);
        var tokenLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[TokenLengthOffset..]);
        if (tokenLength == 0) { error = "token is empty"; return false; }
        if (tokenLength > MaxTokenLength) { error = "token is too long"; return false; }
        if (buffer.Length < FixedPreambleLength + tokenLength) { error = "preamble is truncated"; return false; }

        string token;
        try { token = Encoding.UTF8.GetString(buffer.Slice(FixedPreambleLength, tokenLength)); }
        catch (ArgumentException) { error = "token is not valid UTF-8"; return false; }

        preamble = new RelayPreamble(sessionId, role, token);
        return true;
    }

    /// <summary>جانب الـ Relay (WEEK 5/6) والاختبارات: يقرأ الرأس الثابت ثم التوكن بطوله.</summary>
    public static async Task<RelayPreamble> ReadPreambleAsync(Stream stream, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultIoTimeout);
        try
        {
            var header = new byte[FixedPreambleLength];
            await ReadExactlyAsync(stream, header, "relay preamble header", cts.Token).ConfigureAwait(false);
            if (!header.AsSpan(0, MagicLength).SequenceEqual(Magic)) throw new RelayProtocolException("bad magic");
            if (header[MagicLength] != Version) throw new RelayProtocolException($"unsupported version {header[MagicLength]}");

            var tokenLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(TokenLengthOffset));
            if (tokenLength is 0 or > MaxTokenLength) throw new RelayProtocolException("bad token length");

            var full = new byte[FixedPreambleLength + tokenLength];
            header.CopyTo(full, 0);
            await ReadExactlyAsync(stream, full.AsMemory(FixedPreambleLength), "relay token", cts.Token).ConfigureAwait(false);
            if (!TryParsePreamble(full, out var preamble, out var error)) throw new RelayProtocolException(error!);
            return preamble!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("timed out reading the relay preamble");
        }
    }

    // ---------- الرد ----------

    public static byte[] BuildReply(RelayStatus status) => new[] { Version, (byte)status };

    public static async Task<RelayStatus> ReadReplyAsync(Stream stream, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultIoTimeout);
        var reply = new byte[ReplyLength];
        try
        {
            await ReadExactlyAsync(stream, reply, "relay reply", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("timed out waiting for the relay reply");
        }
        if (reply[0] != Version) throw new RelayProtocolException($"unsupported relay reply version {reply[0]}");
        return (RelayStatus)reply[1];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, string what, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new RelayProtocolException($"connection closed while reading {what}");
            read += n;
        }
    }
}
