using System.Security.Cryptography;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Auth;

/// <summary>AUTH1/AUTH2 failed (a wrong verification, a wrong version, or the other side closing before replying). The caller closes the connection.</summary>
public sealed class AuthFailedException : Exception
{
    public AuthFailedException(string message, Exception? inner = null, bool peerClosed = false) : base(message, inner)
        => PeerClosed = peerClosed;

    /// <summary>
    /// The other side closed or dropped before the message completed, rather than its message failing verification. The difference is not cosmetic:
    /// <b>closing with no reply is what the host does to the losing connection</b> (docs/protocol.md section 2 step 5), so it may not
    /// be counted as an unauthorised access attempt; a verification failure, on the other hand, is the strongest evidence possible of the opposite (a party that knows the shape and not the secret).
    /// </summary>
    public bool PeerClosed { get; }
}

/// <summary>What the host needs from AUTH1 to build AUTH2 (client_random).</summary>
public sealed class Auth1Result
{
    internal Auth1Result(byte[] clientRandom) => ClientRandom = clientRandom;
    public byte[] ClientRandom { get; }
}

/// <summary>
/// Authentication inside TLS, exactly per docs/protocol.md section 4.
/// AUTH1 (guest -> host), 81 bytes: u8 version=1 | 16B session_id (big-endian, RFC 4122) | 32B client_random | 32B mac1.
/// AUTH2 (host -> guest), 32 bytes: mac2.
/// mac = HMAC-SHA256(secret, label || session_id || client_random || listener_cert_fp), compared in constant time.
/// The host writes no byte before verifying AUTH1. A failure throws AuthFailedException with no reply at all.
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

    /// <summary>The session id's bytes in big-endian order as in RFC 4122 (not Guid.ToByteArray()'s default order).</summary>
    public static byte[] SessionIdBytes(Guid sessionId) => sessionId.ToByteArray(bigEndian: true);

    // ---------- Guest ----------

    /// <summary>The guest's side: it sends AUTH1 then waits for AUTH2 and verifies it. listenerCertFp = the fingerprint of whoever is the TLS server in this connection.</summary>
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

    /// <summary>The host's side: it reads AUTH1 and verifies it without writing a byte. It throws AuthFailedException on failure.</summary>
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

    /// <summary>The synchronous verification of AUTH1 (81 bytes). It returns client_random on success.</summary>
    private static byte[] VerifyAuth1Bytes(ReadOnlySpan<byte> auth1, SessionMaterial material, ReadOnlySpan<byte> listenerCertFp)
    {
        if (auth1[0] != Version) throw new AuthFailedException("AUTH1 has unsupported version");

        var sessionId = SessionIdBytes(material.SessionId);
        var receivedSessionId = auth1.Slice(1, SessionIdLength);
        var clientRandom = auth1.Slice(1 + SessionIdLength, RandomLength).ToArray();
        var receivedMac = auth1.Slice(1 + SessionIdLength + RandomLength, MacLength);

        // Comparing the id and the MAC together is constant time and does not reveal which one failed.
        var sessionOk = CryptographicOperations.FixedTimeEquals(sessionId, receivedSessionId);
        var expectedMac = ComputeMac(Label1, material.Secret, sessionId, clientRandom, listenerCertFp);
        var macOk = CryptographicOperations.FixedTimeEquals(expectedMac, receivedMac);
        if (!(sessionOk & macOk)) throw new AuthFailedException("AUTH1 verification failed");

        return clientRandom;
    }

    /// <summary>The host's side: it sends AUTH2 after VerifyAuth1Async succeeds. It is called only for the connection the host decided to keep.</summary>
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

    /// <summary>The host's side combined: verify AUTH1 then send AUTH2. The timeout covers both steps together.</summary>
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
            throw new AuthFailedException($"peer closed the connection before {what}", e, peerClosed: true);
        }
        catch (IOException e)
        {
            throw new AuthFailedException($"connection failed before {what}", e, peerClosed: true);
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
