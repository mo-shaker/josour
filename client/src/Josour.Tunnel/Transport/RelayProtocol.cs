using System.Buffers.Binary;
using System.Text;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>The relay handshake's status as the server returns it in one byte.</summary>
public enum RelayStatus : byte
{
    /// <summary>The two sides are paired; everything after this byte is opaque bytes (TLS between the two machines).</summary>
    Paired = 0,
    Unauthorized = 1,
    UnknownSession = 2,
    /// <summary>The other side did not arrive within the relay's pairing timeout.</summary>
    NoPeer = 3,
    /// <summary>The same role is already registered for this session.</summary>
    Busy = 4,
    ProtocolError = 5,
    Internal = 6,
}

/// <summary>The relay handshake failed with an explicit status.</summary>
public sealed class RelayRejectedException : IOException
{
    public RelayRejectedException(RelayStatus status)
        : base($"relay rejected the session: {RelayProtocol.ToWire(status)}")
        => Status = status;

    public RelayStatus Status { get; }
}

/// <summary>The relay preamble is invalid (the magic, the version or the lengths).</summary>
public sealed class RelayProtocolException : IOException
{
    public RelayProtocolException(string message) : base(message) { }
}

/// <summary>What the relay reads from a side before pairing.</summary>
public sealed record RelayPreamble(Guid SessionId, TunnelRole Role, string Token);

/// <summary>
/// The small wire contract between the client and the relay (ADR-0003 items 4 and 5). The aim is for a relay to be "a configuration flip" rather than a redesign:
/// only the transport changes, and TLS and the authentication (docs/protocol.md sections 3 and 4) stay between the two machines on top of the relay's opaque bytes.
///
/// The preamble (client -> relay), a fixed 24 bytes + the token:
/// <code>
///   4B  magic = "RBRL"
///   u8  version = 1
///   u8  role    (0 = guest, 1 = host)   <- the relay pairs two different roles for the same session_id
///   16B session_id (UUID bytes, big-endian, the same order as AUTH1)
///   u16 token_length (big-endian, 1..1024)
///   NB  token (signed by the backend server, UTF-8)
/// </code>
/// The reply (relay -> client), two bytes: <c>u8 version = 1 | u8 status</c> (<see cref="RelayStatus"/>).
///
/// WEEK 5/6: the server side (the relay service that reads the preamble, verifies the token, pairs the two sides and pumps the bytes) is not built
/// unless the decision gate in ADR-0003 closes (below 85% direct success within 10 seconds on 10 real pairs).
/// Only the client side is here, with <see cref="ReadPreambleAsync"/> and <see cref="BuildReply"/> so a fake in-process relay can exercise it.
/// </summary>
public static class RelayProtocol
{
    public const byte Version = 1;
    public const int MagicLength = 4;
    public const int SessionIdLength = 16;
    public const int TokenLengthOffset = MagicLength + 1 + 1 + SessionIdLength;
    /// <summary>The length of the preamble's fixed part before the token.</summary>
    public const int FixedPreambleLength = TokenLengthOffset + 2; // 24
    public const int MaxTokenLength = 1024;
    public const int ReplyLength = 2;
    public const byte GuestRole = 0;
    public const byte HostRole = 1;

    private static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decoding the token throws on any invalid byte instead of replacing it with U+FFFD as the default <see cref="Encoding.UTF8"/>
    /// does. Without this the "token is not valid UTF-8" branch was dead, and the relay handed verification a <b>rewritten</b>
    /// token rather than the token that was sent — that is, the wire was not refusing a corrupt payload but silently rewriting it.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

    // ---------- The preamble ----------

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

    /// <summary>Parses a complete preamble in the buffer. false with a descriptive reason on any fault (with no secrets in the text).</summary>
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
        // DecoderFallbackException inherits from ArgumentException.
        try { token = StrictUtf8.GetString(buffer.Slice(FixedPreambleLength, tokenLength)); }
        catch (ArgumentException) { error = "token is not valid UTF-8"; return false; }

        preamble = new RelayPreamble(sessionId, role, token);
        return true;
    }

    /// <summary>The relay's side (WEEK 5/6) and the tests: it reads the fixed header and then the token at its length.</summary>
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

    // ---------- The reply ----------

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
