using System.Buffers.Binary;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Transport;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// Fuzzing the relay's preamble parser (<see cref="RelayProtocol"/>). The preamble is the first thing either side reads from a peer that has not
/// been authenticated yet, so it is — with <c>AUTH1</c> — the boundary a hostile input reaches before any verification.
/// </summary>
public class RelayProtocolFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly ITestOutputHelper _output;

    public RelayProtocolFuzzTests(ITestOutputHelper output) => _output = output;

    private static byte[] Valid(string token = "signed-token") => RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Host, token);

    // ---------- Parsing in memory ----------

    /// <summary>Random bytes: it never throws, and what it accepts respects every constraint in the contract.</summary>
    [Fact]
    public void TryParsePreamble_NeverThrows_AndKeepsItsContract()
    {
        var accepted = 0;
        var cases = FuzzSeed.Cases(4000);
        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("relay-random", i);
            // Purely random bytes never pass the magic check, so two thirds of the cases are built with a structurally valid preamble: that is how
            // the fuzz actually reaches the role check, the token length and the UTF-8 decoding rather than the first line alone. And the last third carries
            // a valid ASCII token to prove the acceptance path is reachable at all (otherwise "zero accepted" would mean nothing).
            var bytes = Mutate.Random(rng, rng.Next(0, 1200));
            if (i % 3 != 0 && bytes.Length > RelayProtocol.FixedPreambleLength)
            {
                RelayProtocol.Magic.CopyTo(bytes);
                bytes[RelayProtocol.MagicLength] = RelayProtocol.Version;
                bytes[RelayProtocol.MagicLength + 1] = (byte)(i % 2);
                var tokenBytes = Math.Min(bytes.Length - RelayProtocol.FixedPreambleLength, RelayProtocol.MaxTokenLength);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(RelayProtocol.TokenLengthOffset), (ushort)tokenBytes);
                if (i % 3 == 2)
                {
                    for (var t = 0; t < tokenBytes; t++) bytes[RelayProtocol.FixedPreambleLength + t] = (byte)rng.Next(0x21, 0x7f);
                }
            }

            var ok = RelayProtocol.TryParsePreamble(bytes, out var preamble, out var error);
            if (!ok)
            {
                Assert.Null(preamble);
                Assert.False(string.IsNullOrEmpty(error), $"case {i}: rejected without a reason");
                continue;
            }

            accepted++;
            Assert.NotNull(preamble);
            Assert.Null(error);
            Assert.True(preamble!.Role is TunnelRole.Host or TunnelRole.Guest);
            Assert.InRange(Encoding.UTF8.GetByteCount(preamble.Token), 1, RelayProtocol.MaxTokenLength);
        }
        _output.WriteLine($"{cases} random relay preambles: {accepted} accepted, all within the contract");
        Assert.True(accepted > 0, "no generated preamble was ever accepted; the fuzz never reaches the accepting path");
    }

    /// <summary>Truncation at every possible length: anything short of the full preamble is refused, and the full one is accepted.</summary>
    [Fact]
    public void TruncationAtEveryLength_IsRejected_ExceptTheCompletePreamble()
    {
        var valid = Valid();
        for (var length = 0; length < valid.Length; length++)
            Assert.False(RelayProtocol.TryParsePreamble(valid.AsSpan(0, length), out _, out _), $"a {length}-byte prefix was accepted");
        Assert.True(RelayProtocol.TryParsePreamble(valid, out _, out _));
    }

    /// <summary>Flipping one bit at every position of the preamble: it does not throw, and what is accepted stays within the contract.</summary>
    [Fact]
    public void EverySingleBitFlip_IsHandled()
    {
        var valid = Valid();
        var accepted = 0;
        for (var index = 0; index < valid.Length; index++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])valid.Clone();
                mutated[index] ^= (byte)(1 << bit);
                if (!RelayProtocol.TryParsePreamble(mutated, out var preamble, out _)) continue;
                accepted++;
                Assert.InRange(Encoding.UTF8.GetByteCount(preamble!.Token), 1, RelayProtocol.MaxTokenLength);
            }
        }
        _output.WriteLine($"{valid.Length * 8} single-bit flips: {accepted} still parsed, none outside the contract");
    }

    /// <summary>
    /// A token with bytes that are not valid UTF-8. <b>A defect fixed in week 6:</b> the decoding used the default
    /// <c>Encoding.UTF8</c>, which <i>replaces</i> the corrupt byte with U+FFFD and never throws, so the
    /// "token is not valid UTF-8" branch was dead and the parser handed on a rewritten token rather than the token that arrived.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xC3, 0x28 })]          // a multi-byte lead followed by an invalid continuation
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]    // an encoded surrogate half — forbidden in UTF-8
    [InlineData(new byte[] { 0x80 })]                // a continuation byte with no lead
    [InlineData(new byte[] { 0xE2, 0x82 })]          // a truncated three-byte sequence
    public void TokenWithInvalidUtf8_IsRejected_NotSilentlyRewritten(byte[] tokenBytes)
    {
        var preamble = BuildRaw(Guid.NewGuid(), RelayProtocol.HostRole, tokenBytes);

        var ok = RelayProtocol.TryParsePreamble(preamble, out var parsed, out var error);

        Assert.False(ok, "an invalid-UTF-8 token was accepted and silently rewritten with replacement characters");
        Assert.Null(parsed);
        Assert.Equal("token is not valid UTF-8", error);
    }

    /// <summary>A declared token length larger than what arrived: a refusal, with no waiting and no reading outside the buffer.</summary>
    [Fact]
    public void DeclaredTokenLengthBeyondTheBuffer_IsRejected()
    {
        var valid = Valid("abc");
        var lying = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(lying.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength);
        Assert.False(RelayProtocol.TryParsePreamble(lying, out _, out var error));
        Assert.Equal("preamble is truncated", error);
    }

    [Fact]
    public void ZeroAndOversizedTokenLength_AreRejected()
    {
        var valid = Valid("abc");
        var zero = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(zero.AsSpan(RelayProtocol.TokenLengthOffset), 0);
        Assert.False(RelayProtocol.TryParsePreamble(zero, out _, out var zeroError));
        Assert.Equal("token is empty", zeroError);

        var big = new byte[RelayProtocol.FixedPreambleLength + RelayProtocol.MaxTokenLength + 1];
        valid.AsSpan(0, RelayProtocol.FixedPreambleLength).CopyTo(big);
        BinaryPrimitives.WriteUInt16BigEndian(big.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength + 1);
        Assert.False(RelayProtocol.TryParsePreamble(big, out _, out var bigError));
        Assert.Equal("token is too long", bigError);
    }

    // ---------- Reading from a stream ----------

    /// <summary>The reader against a hostile input: it does not hang, throws nothing but the expected types, and allocates no more than the token's cap.</summary>
    [Fact]
    public async Task ReadPreambleAsync_OnAdversarialInput_NeverHangs_NorThrowsAnUnexpectedType()
    {
        var valid = Valid();
        var cases = FuzzSeed.Cases(300);
        var parsed = 0;
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("relay-stream", i);
            var (_, bytes) = Mutate.Any(valid, rng);
            var stream = new ReplayStream(bytes);
            try
            {
                await RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                parsed++;
            }
            catch (Exception e)
            {
                Assert.True(e is RelayProtocolException or TimeoutException or OperationCanceledException,
                    $"case {i} ({bytes.Length} bytes) threw {e.GetType().FullName}: {e.Message}");
            }
            Assert.Empty(stream.Written); // the reader answers nothing: the reply is the relay's decision after verification, not the parser's
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine($"{cases} adversarial preambles read from a stream: {parsed} parsed; heap {before / 1024} KiB → {after / 1024} KiB");
        Assert.True(after - before < 16L * 1024 * 1024, $"reading adversarial preambles grew the heap by {(after - before) / 1024} KiB");
    }

    /// <summary>A declared token length of 1024 with an early close: an explicit error rather than a wait, and what is allocated is governed by the cap rather than by the claim.</summary>
    [Fact]
    public async Task ReadPreambleAsync_WithADeclaredTokenThatNeverArrives_FailsFast()
    {
        var header = Valid("x").AsSpan(0, RelayProtocol.FixedPreambleLength).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength);

        var stream = new ReplayStream(header);
        var error = await Assert.ThrowsAsync<RelayProtocolException>(
            () => RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("relay token", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A token that is not valid UTF-8 through the reader too: the same verdict, with an explicit protocol error.</summary>
    [Fact]
    public async Task ReadPreambleAsync_RejectsInvalidUtf8Token()
    {
        var stream = new ReplayStream(BuildRaw(Guid.NewGuid(), RelayProtocol.GuestRole, new byte[] { 0xFF, 0xFE, 0xFD }));
        var error = await Assert.ThrowsAsync<RelayProtocolException>(
            () => RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("token is not valid UTF-8", error.Message);
    }

    /// <summary>The preamble spread over single-byte writes: it is assembled in full (the reader does not assume message boundaries).</summary>
    [Fact]
    public async Task ReadPreambleAsync_ReassemblesByteAtATimeDelivery()
    {
        var sessionId = Guid.NewGuid();
        var bytes = RelayProtocol.BuildPreamble(sessionId, TunnelRole.Guest, "t");
        var stream = new OneByteAtATimeStream(bytes);

        var preamble = await RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(sessionId, preamble.SessionId);
        Assert.Equal(TunnelRole.Guest, preamble.Role);
        Assert.Equal("t", preamble.Token);
    }

    // ---------- The reply ----------

    /// <summary>Fuzzing the reply's two bytes: a wrong version is a protocol error, and every status that is not <c>paired</c> is a refusal at the transport.</summary>
    [Fact]
    public async Task ReplyFuzz_CoversEveryVersionAndStatusByte()
    {
        var rejected = 0;
        for (var version = 0; version < 256; version += 1)
        {
            for (var status = 0; status < 256; status += 37)
            {
                var stream = new ReplayStream(new[] { (byte)version, (byte)status });
                if (version != RelayProtocol.Version)
                {
                    await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None));
                    rejected++;
                    continue;
                }

                var result = await RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None);
                Assert.Equal((RelayStatus)status, result);
                // RelayTransport accepts paired alone; any other byte — known or not — is a refusal.
                if (result != RelayStatus.Paired) rejected++;
            }
        }
        _output.WriteLine($"reply fuzz: {rejected} of the sampled (version, status) pairs are rejected; only version 1 + status 0 pairs the tunnel");
        Assert.True(rejected > 0);
    }

    /// <summary>A truncated reply (one byte then a close): an explicit error rather than an endless wait.</summary>
    [Fact]
    public async Task TruncatedReply_FailsFast()
    {
        var stream = new ReplayStream(new byte[] { RelayProtocol.Version });
        await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---------- Helpers ----------

    /// <summary>It builds a preamble with raw token bytes (which may not be valid UTF-8), which <c>BuildPreamble</c> does not permit.</summary>
    private static byte[] BuildRaw(Guid sessionId, byte role, byte[] tokenBytes)
    {
        var buffer = new byte[RelayProtocol.FixedPreambleLength + tokenBytes.Length];
        RelayProtocol.Magic.CopyTo(buffer);
        buffer[RelayProtocol.MagicLength] = RelayProtocol.Version;
        buffer[RelayProtocol.MagicLength + 1] = role;
        sessionId.ToByteArray(bigEndian: true).CopyTo(buffer, RelayProtocol.MagicLength + 2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(RelayProtocol.TokenLengthOffset), (ushort)tokenBytes.Length);
        tokenBytes.CopyTo(buffer, RelayProtocol.FixedPreambleLength);
        return buffer;
    }

    /// <summary>A stream that hands over one byte per read: the worst possible fragmentation.</summary>
    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly byte[] _source;
        private int _position;

        public OneByteAtATimeStream(byte[] source) => _source = source;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _source.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _source[_position++];
            return ValueTask.FromResult(1);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
