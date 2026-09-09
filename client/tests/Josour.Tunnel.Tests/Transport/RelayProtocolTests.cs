using System.Buffers.Binary;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Auth;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests.Transport;

/// <summary>مقدمة الـ Relay ورده: الترميز والتحليل وحالات الرفض (ADR-0003 البند 5).</summary>
public class RelayProtocolTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.relay-session-token";

    [Theory]
    [InlineData(TunnelRole.Guest, RelayProtocol.GuestRole)]
    [InlineData(TunnelRole.Host, RelayProtocol.HostRole)]
    public void Preamble_RoundTrips(TunnelRole role, byte roleByte)
    {
        var sessionId = Guid.NewGuid();
        var bytes = RelayProtocol.BuildPreamble(sessionId, role, Token);

        Assert.Equal(RelayProtocol.FixedPreambleLength + Encoding.UTF8.GetByteCount(Token), bytes.Length);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("RBRL"u8));
        Assert.Equal(RelayProtocol.Version, bytes[4]);
        Assert.Equal(roleByte, bytes[5]);
        // معرّف الجلسة بترتيب AUTH1 نفسه (RFC 4122 big-endian) لا ترتيب Guid الافتراضي
        Assert.True(bytes.AsSpan(6, 16).SequenceEqual(AuthHandshake.SessionIdBytes(sessionId)));

        Assert.True(RelayProtocol.TryParsePreamble(bytes, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(sessionId, parsed!.SessionId);
        Assert.Equal(role, parsed.Role);
        Assert.Equal(Token, parsed.Token);
    }

    [Fact]
    public void Preamble_CarriesUnicodeTokenIntact()
    {
        var token = "توكن-جلسة-٢٠٢٦";
        var bytes = RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Host, token);
        Assert.True(RelayProtocol.TryParsePreamble(bytes, out var parsed, out _));
        Assert.Equal(token, parsed!.Token);
    }

    [Fact]
    public void Preamble_RejectsEmptyOrOversizedToken()
    {
        Assert.Throws<ArgumentException>(() => RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Guest, ""));
        Assert.Throws<ArgumentNullException>(() => RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Guest, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Guest, new string('t', RelayProtocol.MaxTokenLength + 1)));
    }

    [Fact]
    public void Parse_RejectsBadMagic_Version_Role_And_Truncation()
    {
        var good = RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Guest, Token);

        Assert.False(RelayProtocol.TryParsePreamble(good.AsSpan(0, RelayProtocol.FixedPreambleLength - 1), out _, out var shortError));
        Assert.Contains("fixed header", shortError);

        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.False(RelayProtocol.TryParsePreamble(badMagic, out _, out var magicError));
        Assert.Equal("bad magic", magicError);

        var badVersion = (byte[])good.Clone();
        badVersion[4] = 2;
        Assert.False(RelayProtocol.TryParsePreamble(badVersion, out _, out var versionError));
        Assert.Contains("unsupported version", versionError);

        var badRole = (byte[])good.Clone();
        badRole[5] = 9;
        Assert.False(RelayProtocol.TryParsePreamble(badRole, out _, out var roleError));
        Assert.Contains("bad role", roleError);

        var zeroToken = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(zeroToken.AsSpan(RelayProtocol.TokenLengthOffset), 0);
        Assert.False(RelayProtocol.TryParsePreamble(zeroToken, out _, out var tokenError));
        Assert.Equal("token is empty", tokenError);

        Assert.False(RelayProtocol.TryParsePreamble(good.AsSpan(0, good.Length - 3), out _, out var truncatedError));
        Assert.Equal("preamble is truncated", truncatedError);
    }

    [Fact]
    public async Task ReadPreambleAsync_ReassemblesFragmentedWrites()
    {
        var sessionId = Guid.NewGuid();
        var bytes = RelayProtocol.BuildPreamble(sessionId, TunnelRole.Host, Token);
        var (client, server) = await Loopback.CreatePairAsync();
        await using (client)
        await using (server)
        {
            var read = RelayProtocol.ReadPreambleAsync(server, TimeSpan.FromSeconds(5), CancellationToken.None);
            await client.WriteAsync(bytes.AsMemory(0, 7));
            await client.FlushAsync();
            await Task.Delay(30);
            await client.WriteAsync(bytes.AsMemory(7));
            await client.FlushAsync();

            var preamble = await read.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(sessionId, preamble.SessionId);
            Assert.Equal(TunnelRole.Host, preamble.Role);
            Assert.Equal(Token, preamble.Token);
        }
    }

    [Fact]
    public async Task ReadPreambleAsync_ThrowsOnBadMagicAndOnEarlyClose()
    {
        var (client, server) = await Loopback.CreatePairAsync();
        await using (client)
        await using (server)
        {
            await client.WriteAsync(new byte[RelayProtocol.FixedPreambleLength]);
            await client.FlushAsync();
            await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadPreambleAsync(server, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        var (a, b) = await Loopback.CreatePairAsync();
        await using (a)
        await using (b)
        {
            var read = RelayProtocol.ReadPreambleAsync(b, TimeSpan.FromSeconds(5), CancellationToken.None);
            await a.WriteAsync(RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Guest, Token).AsMemory(0, 10));
            await a.FlushAsync();
            await a.DisposeAsync();
            await Assert.ThrowsAsync<RelayProtocolException>(() => read);
        }
    }

    [Fact]
    public async Task ReadPreambleAsync_TimesOut()
    {
        var (client, server) = await Loopback.CreatePairAsync();
        await using (client)
        await using (server)
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => RelayProtocol.ReadPreambleAsync(server, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Reply_RoundTrips_AndRejectsUnknownVersion()
    {
        Assert.Equal(new byte[] { RelayProtocol.Version, 0 }, RelayProtocol.BuildReply(RelayStatus.Paired));

        var (client, server) = await Loopback.CreatePairAsync();
        await using (client)
        await using (server)
        {
            await client.WriteAsync(RelayProtocol.BuildReply(RelayStatus.NoPeer));
            await client.FlushAsync();
            Assert.Equal(RelayStatus.NoPeer, await RelayProtocol.ReadReplyAsync(server, TimeSpan.FromSeconds(5), CancellationToken.None));

            await client.WriteAsync(new byte[] { 7, 0 });
            await client.FlushAsync();
            await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadReplyAsync(server, TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(RelayStatus.Paired, "paired")]
    [InlineData(RelayStatus.Unauthorized, "unauthorized")]
    [InlineData(RelayStatus.UnknownSession, "unknown_session")]
    [InlineData(RelayStatus.NoPeer, "no_peer")]
    [InlineData(RelayStatus.Busy, "busy")]
    [InlineData(RelayStatus.ProtocolError, "protocol_error")]
    [InlineData(RelayStatus.Internal, "internal")]
    public void StatusNames_AreStable(RelayStatus status, string wire) => Assert.Equal(wire, RelayProtocol.ToWire(status));
}
