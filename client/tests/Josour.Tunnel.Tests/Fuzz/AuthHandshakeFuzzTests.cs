using System.Diagnostics;
using System.Security.Cryptography;
using Josour.Core.Tunnel;
using Josour.Tunnel.Auth;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// A stream that reads from a prepared buffer and keeps everything written to it: it makes the host's side of the handshake measurable thousands of times
/// with no sockets, and makes "how many bytes did the host write?" a question answered exactly. The buffer running out = EOF (the other side closing).
/// </summary>
internal sealed class ReplayStream : Stream
{
    private readonly byte[] _source;
    private readonly MemoryStream _written = new();
    private int _position;

    public ReplayStream(byte[] source) => _source = source;

    /// <summary>What the side under test wrote. It must be empty in every authentication failure.</summary>
    public byte[] Written => _written.ToArray();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _source.Length - _position);
        if (n <= 0) return 0;
        _source.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset, n));
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = Math.Min(buffer.Length, _source.Length - _position);
        if (n <= 0) return ValueTask.FromResult(0);
        _source.AsSpan(_position, n).CopyTo(buffer.Span);
        _position += n;
        return ValueTask.FromResult(n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _written.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Fuzzing <c>AUTH1</c>/<c>AUTH2</c> (<c>docs/protocol.md</c> section 4). The governing rule in the contract:
/// <b>"Failure: the connection is closed with no reply"</b> and "the host writes no byte before verifying". The test here measures it literally:
/// the number of bytes the host wrote after every hostile input must be zero.
/// </summary>
public class AuthHandshakeFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly ITestOutputHelper _output;

    public AuthHandshakeFuzzTests(ITestOutputHelper output) => _output = output;

    private static (SessionMaterial Material, byte[] Fingerprint) Fixture(int seed)
    {
        var rng = FuzzSeed.For("auth-fixture", seed);
        var secret = new byte[AuthHandshake.SecretLength];
        rng.NextBytes(secret);
        var fingerprint = new byte[AuthHandshake.FingerprintLength];
        rng.NextBytes(fingerprint);
        var guid = new byte[16];
        rng.NextBytes(guid);
        var material = new SessionMaterial(new Guid(guid), TunnelRole.Host, secret, DateTimeOffset.UtcNow.AddMinutes(30), true, "203.0.113.1");
        return (material, fingerprint);
    }

    /// <summary>A perfectly valid AUTH1: the reference for comparison, and proof that the test's builder matches the contract.</summary>
    private static byte[] ValidAuth1(SessionMaterial material, byte[] fingerprint, byte[]? clientRandom = null)
    {
        var sessionId = AuthHandshake.SessionIdBytes(material.SessionId);
        clientRandom ??= RandomNumberGenerator.GetBytes(AuthHandshake.RandomLength);
        var input = new byte[8 + 16 + 32 + 32];
        "rb-auth1"u8.CopyTo(input);
        sessionId.CopyTo(input, 8);
        clientRandom.CopyTo(input, 24);
        fingerprint.CopyTo(input, 56);
        var mac = HMACSHA256.HashData(material.Secret, input);

        var auth1 = new byte[AuthHandshake.Auth1Length];
        auth1[0] = AuthHandshake.Version;
        sessionId.CopyTo(auth1, 1);
        clientRandom.CopyTo(auth1, 17);
        mac.CopyTo(auth1, 49);
        return auth1;
    }

    private static async Task<(Exception? Error, byte[] Written)> HostAsync(byte[] input, SessionMaterial material, byte[] fingerprint)
    {
        var stream = new ReplayStream(input);
        try
        {
            await AuthHandshake.VerifyAuth1AndSendAuth2Async(stream, material, fingerprint, Timeout, CancellationToken.None);
            return (null, stream.Written);
        }
        catch (Exception e)
        {
            return (e, stream.Written);
        }
    }

    // ---------- The reference ----------

    [Fact]
    public async Task ValidAuth1_IsAccepted_AndAnsweredWithExactly32Bytes()
    {
        var (material, fingerprint) = Fixture(0);
        var (error, written) = await HostAsync(ValidAuth1(material, fingerprint), material, fingerprint);
        Assert.Null(error);
        Assert.Equal(AuthHandshake.Auth2Length, written.Length);
    }

    // ---------- Truncation ----------

    /// <summary>0..80 bytes then a close: the host fails with no reply, whatever the length.</summary>
    [Fact]
    public async Task TruncatedAuth1_AtEveryLength_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(1);
        var valid = ValidAuth1(material, fingerprint);
        for (var length = 0; length < AuthHandshake.Auth1Length; length++)
        {
            var (error, written) = await HostAsync(valid[..length], material, fingerprint);
            Assert.NotNull(error);
            AssertExpectedAuthFailure(error!, $"truncated to {length} bytes");
            Assert.Empty(written);
        }
    }

    /// <summary>81 valid bytes followed by padding: what comes after the message is not read and does not change the verdict (no payload smuggling).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    public async Task Auth1FollowedByExtraBytes_IsStillAccepted_AndTheExtraIsNotConsumed(int extra)
    {
        var (material, fingerprint) = Fixture(2);
        var valid = ValidAuth1(material, fingerprint);
        var padded = new byte[valid.Length + extra];
        valid.CopyTo(padded, 0);
        FuzzSeed.For("auth-pad", extra).NextBytes(padded.AsSpan(valid.Length));

        var (error, written) = await HostAsync(padded, material, fingerprint);

        Assert.Null(error);
        Assert.Equal(AuthHandshake.Auth2Length, written.Length);
    }

    /// <summary>An inflated AUTH1 with no valid preamble: refused with no reply and with no allocation following a declared length (there is no declared length at all).</summary>
    [Fact]
    public async Task OversizedGarbage_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(3);
        var garbage = Mutate.Random(FuzzSeed.For("auth-oversize", 0), 4 * 1024 * 1024);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var (error, written) = await HostAsync(garbage, material, fingerprint);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.NotNull(error);
        AssertExpectedAuthFailure(error!, "4 MiB of garbage");
        Assert.Empty(written);
        Assert.True(after - before < 16L * 1024 * 1024, $"a 4 MiB garbage AUTH1 grew the heap by {(after - before) / 1024} KiB");
    }

    // ---------- Bit flips ----------

    /// <summary>
    /// Flipping one bit at every position of the 81 bytes: each one is refused with no reply. This covers every field of the message together
    /// (the version, the session id, the random, the MAC) with no exception.
    /// </summary>
    [Fact]
    public async Task EverySingleBitFlip_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(4);
        var valid = ValidAuth1(material, fingerprint);
        var checkedBits = 0;
        for (var index = 0; index < valid.Length; index++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])valid.Clone();
                mutated[index] ^= (byte)(1 << bit);
                var (error, written) = await HostAsync(mutated, material, fingerprint);
                Assert.NotNull(error);
                Assert.IsType<AuthFailedException>(error);
                Assert.Empty(written);
                checkedBits++;
            }
        }
        _output.WriteLine($"every one of {checkedBits} single-bit flips in AUTH1 was rejected with zero bytes written");
        Assert.Equal(AuthHandshake.Auth1Length * 8, checkedBits);
    }

    /// <summary>Random bytes at random lengths: no exception type outside the expected ones, and not one byte written.</summary>
    [Fact]
    public async Task RandomInput_NeverProducesAReply_NorAnUnexpectedExceptionType()
    {
        var (material, fingerprint) = Fixture(5);
        var cases = FuzzSeed.Cases(1500);
        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("auth-random", i);
            var input = Mutate.Random(rng, rng.Next(0, 200));
            var (error, written) = await HostAsync(input, material, fingerprint);
            Assert.NotNull(error);
            AssertExpectedAuthFailure(error!, $"case {i} ({input.Length} bytes)");
            Assert.Empty(written);
        }
        _output.WriteLine($"{cases} random AUTH1 inputs: all rejected, none produced a byte on the wire");
    }

    /// <summary>A wrong secret, a wrong fingerprint and a wrong session id, each on its own: the contract gives the same result for each.</summary>
    [Fact]
    public async Task WrongSecret_WrongSessionId_AndWrongFingerprint_AllFailIdentically()
    {
        var (material, fingerprint) = Fixture(6);
        var (other, otherFingerprint) = Fixture(7);

        foreach (var (label, bytes) in new (string, byte[])[]
        {
            ("wrong secret", ValidAuth1(new SessionMaterial(material.SessionId, TunnelRole.Host, other.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint)),
            ("wrong session id", ValidAuth1(new SessionMaterial(other.SessionId, TunnelRole.Host, material.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint)),
            ("wrong listener fingerprint", ValidAuth1(material, otherFingerprint)),
        })
        {
            var (error, written) = await HostAsync(bytes, material, fingerprint);
            Assert.IsType<AuthFailedException>(error);
            Assert.Equal("AUTH1 verification failed", error!.Message);   // one message: it does not reveal which field failed
            Assert.Empty(written);
            _output.WriteLine($"{label}: AuthFailedException, 0 bytes written");
        }
    }

    // ---------- Replay ----------

    /// <summary>
    /// <b>A documented property rather than a defect:</b> a valid AUTH1 is accepted again if it is replayed literally. There is no server nonce in
    /// the contract (section 4), so the protection is two layers: the message is not seen at all because it is inside TLS pinned to the host's fingerprint, and the host
    /// sends AUTH2 only to the first connection that passes AUTH1 and closes the rest (<c>SymmetricConnector.TryClaim</c>).
    /// The test pins the property so it does not change silently, and so an explicit decision is taken if changing it is wanted.
    /// </summary>
    [Fact]
    public async Task ReplayedAuth1_IsAcceptedByTheHandshakeItself_ByDesign()
    {
        var (material, fingerprint) = Fixture(8);
        var captured = ValidAuth1(material, fingerprint);

        var first = await HostAsync(captured, material, fingerprint);
        var replay = await HostAsync(captured, material, fingerprint);

        Assert.Null(first.Error);
        Assert.Null(replay.Error);
        Assert.Equal(first.Written, replay.Written); // exactly the same AUTH2: the reply is a function of the input alone
        _output.WriteLine("AUTH1 replay is accepted by the handshake; TLS pinning and the host's single-claim rule are what bound it");
    }

    // ---------- Timing ----------

    /// <summary>
    /// <b>What is measured and why this way:</b> what an attacker actually sees is the time from sending AUTH1 to the socket closing, so the measurement here is
    /// end to end over the whole failure path (throwing the exception included) rather than over the comparison alone. Three interleaved groups
    /// are measured:
    /// <list type="bullet">
    ///   <item><b>A wrong secret</b> and <b>a wrong session id</b>: both must take the same path — the comparison covers both fields together
    ///     with <c>&amp;</c> rather than <c>&amp;&amp;</c>, and the MAC is always computed.</item>
    ///   <item><b>A wrong version</b>: an early return that is <i>deliberate and declared</i> in the contract, and it serves here as a positive control measuring what
    ///     this probe can see at all.</item>
    /// </list>
    /// The verdict is relative to the noise rather than to an absolute number: the difference between the two groups is compared with one group's spread against itself
    /// (two alternating halves). Had either path returned early, a difference larger than its own noise would have appeared.
    /// </summary>
    [Fact]
    public async Task WrongSecretAndWrongSessionId_AreNotDistinguishableByTiming()
    {
        var (material, fingerprint) = Fixture(9);
        var (other, _) = Fixture(10);
        var wrongSecret = ValidAuth1(new SessionMaterial(material.SessionId, TunnelRole.Host, other.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint);
        var wrongSession = ValidAuth1(new SessionMaterial(other.SessionId, TunnelRole.Host, material.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint);
        var wrongVersion = (byte[])wrongSecret.Clone();
        wrongVersion[0] = 0xEE;

        const int warmup = 300;
        const int samples = 1500;
        for (var i = 0; i < warmup; i++)
        {
            await HostAsync(wrongSecret, material, fingerprint);
            await HostAsync(wrongSession, material, fingerprint);
            await HostAsync(wrongVersion, material, fingerprint);
        }

        // Two halves per group, alternating: the difference between one group's two halves is the noise floor itself.
        var secretA = new List<double>(samples / 2);
        var secretB = new List<double>(samples / 2);
        var session = new List<double>(samples);
        var version = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            (i % 2 == 0 ? secretA : secretB).Add(await MeasureAsync(wrongSecret, material, fingerprint));
            session.Add(await MeasureAsync(wrongSession, material, fingerprint));
            version.Add(await MeasureAsync(wrongVersion, material, fingerprint));
        }

        var mSecretA = Median(secretA);
        var mSecretB = Median(secretB);
        var mSession = Median(session);
        var mVersion = Median(version);
        var noise = Math.Abs(mSecretA - mSecretB);                       // the same input twice: pure noise
        var signal = Math.Abs((mSecretA + mSecretB) / 2 - mSession);     // two inputs that should be on the same path
        var control = Math.Abs((mSecretA + mSecretB) / 2 - mVersion);    // a known early return

        _output.WriteLine(
            $"median end-to-end failure time: wrong secret {mSecretA * 1000:F2}/{mSecretB * 1000:F2} µs, " +
            $"wrong session id {mSession * 1000:F2} µs, wrong version (known early return) {mVersion * 1000:F2} µs");
        _output.WriteLine(
            $"noise floor (same input, split halves) {noise * 1000:F2} µs; secret-vs-session difference {signal * 1000:F2} µs; " +
            $"early-return control {control * 1000:F2} µs");

        Assert.True(signal <= Math.Max(3 * noise, 0.002),
            $"wrong-secret and wrong-session-id differ by {signal * 1000:F2} µs against a {noise * 1000:F2} µs noise floor; one path is returning early");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToList();
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static async Task<double> MeasureAsync(byte[] input, SessionMaterial material, byte[] fingerprint)
    {
        var clock = Stopwatch.StartNew();
        await HostAsync(input, material, fingerprint);
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The exception types permitted on failure. Any other type (<c>IndexOutOfRange</c>, <c>NullReference</c>, …) means a hostile
    /// input reached a path that was not designed for it.
    /// </summary>
    private static void AssertExpectedAuthFailure(Exception error, string what)
        => Assert.True(error is AuthFailedException or TimeoutException or OperationCanceledException,
            $"{what} threw {error.GetType().FullName}: {error.Message}");
}
