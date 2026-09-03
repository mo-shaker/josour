using System.Security.Cryptography;
using RouteBridge.Core.Tunnel;

namespace RouteBridge.Tunnel.Auth;

/// <summary>فشل AUTH1/AUTH2 (تحقق خاطئ، إصدار خاطئ، أو إغلاق الطرف الآخر قبل الرد). المستدعي يغلق الاتصال.</summary>
public sealed class AuthFailedException : Exception
{
    public AuthFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>ما يحتاجه المضيف من AUTH1 لبناء AUTH2 (client_random).</summary>
public sealed class Auth1Result
{
    internal Auth1Result(byte[] clientRandom) => ClientRandom = clientRandom;
    public byte[] ClientRandom { get; }
}

/// <summary>
/// المصادقة داخل TLS حسب docs/protocol.md القسم 4 بالضبط.
/// AUTH1 (Guest → Host) 81 بايت: u8 version=1 | 16B session_id (big-endian RFC 4122) | 32B client_random | 32B mac1.
/// AUTH2 (Host → Guest) 32 بايت: mac2.
/// mac = HMAC-SHA256(secret, label || session_id || client_random || listener_cert_fp)، المقارنة ثابتة الزمن.
/// المضيف لا يكتب أي بايت قبل التحقق من AUTH1. الفشل يرمي AuthFailedException بلا أي رد.
/// </summary>
public static class AuthHandshake
{
    public const byte Version = 1;
    public const int SessionIdLength = 16;
    public const int RandomLength = 32;
    public const int MacLength = 32;
    public const int FingerprintLength = 32;
    public const int SecretLength = 32;
    public const int Auth1Length = 1 + SessionIdLength + RandomLength + MacLength; // 81
    public const int Auth2Length = MacLength; // 32

    private static ReadOnlySpan<byte> Label1 => "rb-auth1"u8;
    private static ReadOnlySpan<byte> Label2 => "rb-auth2"u8;

    /// <summary>بايتات معرّف الجلسة بترتيب big-endian كما في RFC 4122 (لا ترتيب Guid.ToByteArray() الافتراضي).</summary>
    public static byte[] SessionIdBytes(Guid sessionId) => sessionId.ToByteArray(bigEndian: true);

    // ---------- Guest ----------

    /// <summary>جانب Guest: يرسل AUTH1 ثم ينتظر AUTH2 ويتحقق منه. listenerCertFp = بصمة شهادة من يعمل TLS Server في هذا الاتصال.</summary>
    public static async Task SendAuth1AndVerifyAuth2Async(Stream stream, SessionMaterial material, byte[] listenerCertFp, TimeSpan timeout, CancellationToken ct)
    {
        Validate(stream, material, listenerCertFp);
        var sessionId = SessionIdBytes(material.SessionId);
        var clientRandom = RandomNumberGenerator.GetBytes(RandomLength);

        var auth1 = new byte[Auth1Length];
        auth1[0] = Version;
        sessionId.CopyTo(auth1, 1);
        clientRandom.CopyTo(auth1, 1 + SessionIdLength);
        ComputeMac(Label1, material.Secret, sessionId, clientRandom, listenerCertFp).CopyTo(auth1, 1 + SessionIdLength + RandomLength);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(auth1, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            var auth2 = new byte[Auth2Length];
            await ReadExactlyOrFailAsync(stream, auth2, "AUTH2", cts.Token).ConfigureAwait(false);

            var expected = ComputeMac(Label2, material.Secret, sessionId, clientRandom, listenerCertFp);
            if (!CryptographicOperations.FixedTimeEquals(expected, auth2))
                throw new AuthFailedException("AUTH2 verification failed");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("authentication timed out waiting for AUTH2");
        }
    }

    // ---------- Host ----------

    /// <summary>جانب Host: يقرأ AUTH1 ويتحقق منه دون كتابة أي بايت. يرمي AuthFailedException عند الفشل.</summary>
    public static async Task<Auth1Result> VerifyAuth1Async(Stream stream, SessionMaterial material, byte[] listenerCertFp, TimeSpan timeout, CancellationToken ct)
    {
        Validate(stream, material, listenerCertFp);
        var auth1 = new byte[Auth1Length];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await ReadExactlyOrFailAsync(stream, auth1, "AUTH1", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("authentication timed out waiting for AUTH1");
        }

        return new Auth1Result(VerifyAuth1Bytes(auth1, material, listenerCertFp));
    }

    /// <summary>التحقق المتزامن من AUTH1 (81 بايت). يعيد client_random عند النجاح.</summary>
    private static byte[] VerifyAuth1Bytes(ReadOnlySpan<byte> auth1, SessionMaterial material, ReadOnlySpan<byte> listenerCertFp)
    {
        if (auth1[0] != Version) throw new AuthFailedException("AUTH1 has unsupported version");

        var sessionId = SessionIdBytes(material.SessionId);
        var receivedSessionId = auth1.Slice(1, SessionIdLength);
        var clientRandom = auth1.Slice(1 + SessionIdLength, RandomLength).ToArray();
        var receivedMac = auth1.Slice(1 + SessionIdLength + RandomLength, MacLength);

        // المقارنة على المعرّف والـ MAC معًا ثابتة الزمن ولا تُفصح أيهما فشل.
        var sessionOk = CryptographicOperations.FixedTimeEquals(sessionId, receivedSessionId);
        var expectedMac = ComputeMac(Label1, material.Secret, sessionId, clientRandom, listenerCertFp);
        var macOk = CryptographicOperations.FixedTimeEquals(expectedMac, receivedMac);
        if (!(sessionOk & macOk)) throw new AuthFailedException("AUTH1 verification failed");

        return clientRandom;
    }

    /// <summary>جانب Host: يرسل AUTH2 بعد نجاح VerifyAuth1Async. يُستدعى فقط للاتصال الذي قرر المضيف إبقاءه.</summary>
    public static async Task SendAuth2Async(Stream stream, SessionMaterial material, byte[] listenerCertFp, Auth1Result auth1, TimeSpan timeout, CancellationToken ct)
    {
        Validate(stream, material, listenerCertFp);
        ArgumentNullException.ThrowIfNull(auth1);
        var mac2 = ComputeMac(Label2, material.Secret, SessionIdBytes(material.SessionId), auth1.ClientRandom, listenerCertFp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(mac2, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("authentication timed out sending AUTH2");
        }
    }

    /// <summary>جانب Host مركّبًا: تحقق من AUTH1 ثم أرسل AUTH2. المهلة تغطي الخطوتين معًا.</summary>
    public static async Task VerifyAuth1AndSendAuth2Async(Stream stream, SessionMaterial material, byte[] listenerCertFp, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var auth1 = await VerifyAuth1Async(stream, material, listenerCertFp, timeout, cts.Token).ConfigureAwait(false);
            await SendAuth2Async(stream, material, listenerCertFp, auth1, timeout, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("authentication timed out");
        }
    }

    // ---------- helpers ----------

    private static byte[] ComputeMac(ReadOnlySpan<byte> label, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> listenerCertFp)
    {
        Span<byte> input = stackalloc byte[8 + SessionIdLength + RandomLength + FingerprintLength];
        label.CopyTo(input);
        sessionId.CopyTo(input[8..]);
        clientRandom.CopyTo(input[(8 + SessionIdLength)..]);
        listenerCertFp.CopyTo(input[(8 + SessionIdLength + RandomLength)..]);
        var mac = new byte[MacLength];
        HMACSHA256.HashData(secret, input, mac);
        return mac;
    }

    private static async Task ReadExactlyOrFailAsync(Stream stream, byte[] buffer, string what, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new AuthFailedException($"peer closed the connection before {what}", e);
        }
        catch (IOException e)
        {
            throw new AuthFailedException($"connection failed before {what}", e);
        }
    }

    private static void Validate(Stream stream, SessionMaterial material, byte[] listenerCertFp)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(listenerCertFp);
        if (material.Secret is null || material.Secret.Length != SecretLength)
            throw new ArgumentException($"session secret must be {SecretLength} bytes", nameof(material));
        if (listenerCertFp.Length != FingerprintLength)
            throw new ArgumentException($"listener certificate fingerprint must be {FingerprintLength} bytes", nameof(listenerCertFp));
    }
}
