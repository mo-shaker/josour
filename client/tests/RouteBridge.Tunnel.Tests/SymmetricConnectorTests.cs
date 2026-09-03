using System.Net;
using System.Text;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.Tunnel.Tests;

public class SymmetricConnectorTests
{
    private sealed class Side : IAsyncDisposable
    {
        public Side(TunnelRole role, Guid sessionId, byte[] secret)
        {
            Material = TestMaterial.Create(role, sessionId, secret);
            Certificate = SessionCertificate.Create(Material.ExpiresAt);
            Listener = new TunnelListener(0, IPAddress.Loopback);
            Candidates = new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", Listener.Port) };
            Connector = new SymmetricConnector(Material, Certificate, Listener, new DirectTransport(), Candidates);
        }

        public SessionMaterial Material { get; }
        public SessionCertificate Certificate { get; }
        public TunnelListener Listener { get; }
        public IReadOnlyList<CandidateEndpoint> Candidates { get; }
        public SymmetricConnector Connector { get; }
        public PeerEndpointInfo AsPeer() => new(Certificate.FingerprintHex, Candidates);

        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            Certificate.Dispose();
        }
    }

    [Fact]
    public async Task BothRoles_Connect_KeepExactlyOneStream_AndExchangeHello()
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        await using var host = new Side(TunnelRole.Host, sessionId, secret);
        await using var guest = new Side(TunnelRole.Guest, sessionId, secret);

        var timeout = TimeSpan.FromSeconds(15);
        var hostTask = host.Connector.ConnectAsync(guest.AsPeer(), timeout, CancellationToken.None);
        var guestTask = guest.Connector.ConnectAsync(host.AsPeer(), timeout, CancellationToken.None);
        var hostOutcome = await hostTask;
        var guestOutcome = await guestTask;

        Assert.True(hostOutcome.Result.Connected, Describe(hostOutcome));
        Assert.True(guestOutcome.Result.Connected, Describe(guestOutcome));
        Assert.Equal(CandidateType.Lan, hostOutcome.Result.WinnerType);
        Assert.Equal(CandidateType.Lan, guestOutcome.Result.WinnerType);
        Assert.Contains(hostOutcome.Result.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(hostOutcome.Result.TlsVersion, guestOutcome.Result.TlsVersion);
        Assert.InRange(hostOutcome.Result.ConnectMs, 0, (int)timeout.TotalMilliseconds);
        Assert.Null(hostOutcome.Result.FailureReason);
        Assert.NotNull(hostOutcome.Connection);
        Assert.NotNull(guestOutcome.Connection);
        Assert.False(host.Listener.IsRunning);
        Assert.False(guest.Listener.IsRunning);

        // اتصال واحد بالضبط اجتاز المصادقة في كل طرف
        Assert.Equal(1, Rows(hostOutcome).Count(r => (string?)r["stage"] == "ok"));
        Assert.Equal(1, Rows(guestOutcome).Count(r => (string?)r["stage"] == "ok"));
        Assert.Equal("lan", hostOutcome.Diagnostics["winner_type"]);

        // hello/hello-ack على الـ stream المصادَق يثبت أن الطرفين يحملان طرفي الاتصال نفسه
        await using var hostStream = hostOutcome.Connection!.Stream;
        await using var guestStream = guestOutcome.Connection!.Stream;
        await WriteLineAsync(guestStream, "hello");
        Assert.Equal("hello", await ReadLineAsync(hostStream));
        await WriteLineAsync(hostStream, "hello-ack");
        Assert.Equal("hello-ack", await ReadLineAsync(guestStream));
    }

    [Fact]
    public async Task Timeout_WhenPeerUnreachable_ReturnsDiagnostics()
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        await using var host = new Side(TunnelRole.Host, sessionId, secret);
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var peer = new PeerEndpointInfo(peerCert.FingerprintHex, new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", Loopback.ClosedPort()) });

        var outcome = await host.Connector.ConnectAsync(peer, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(outcome.Result.Connected);
        Assert.Equal("timeout", outcome.Result.FailureReason);
        Assert.Null(outcome.Result.WinnerType);
        Assert.Null(outcome.Connection);
        Assert.False(host.Listener.IsRunning);
        Assert.Equal("host", outcome.Diagnostics["role"]);
        Assert.Equal(0, outcome.Diagnostics["inbound_attempts"]);
        Assert.Null(outcome.Diagnostics["winner"]);
        var row = Assert.Single(Rows(outcome));
        Assert.Equal("dial", row["direction"]);
        Assert.Equal("lan", row["type"]);
        Assert.Equal("127.0.0.1", row["ip"]);
        Assert.NotNull(row["error"]);
        Assert.IsType<long>(row["ms"]);
    }

    [Fact]
    public async Task WrongSecret_NeverConnects_OnEitherSide()
    {
        var sessionId = Guid.NewGuid();
        await using var host = new Side(TunnelRole.Host, sessionId, TestMaterial.NewSecret());
        await using var guest = new Side(TunnelRole.Guest, sessionId, TestMaterial.NewSecret());

        var timeout = TimeSpan.FromSeconds(3);
        var hostTask = host.Connector.ConnectAsync(guest.AsPeer(), timeout, CancellationToken.None);
        var guestTask = guest.Connector.ConnectAsync(host.AsPeer(), timeout, CancellationToken.None);
        var hostOutcome = await hostTask;
        var guestOutcome = await guestTask;

        Assert.False(hostOutcome.Result.Connected);
        Assert.False(guestOutcome.Result.Connected);
        Assert.Equal("timeout", hostOutcome.Result.FailureReason);
        Assert.Contains(Rows(hostOutcome), r => ((string?)r["error"])?.StartsWith("auth_failed", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(Rows(hostOutcome), r => (string?)r["stage"] == "ok");
        Assert.DoesNotContain(Rows(guestOutcome), r => (string?)r["stage"] == "ok");
    }

    [Fact]
    public async Task WrongPeerFingerprint_NeverConnects()
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        await using var host = new Side(TunnelRole.Host, sessionId, secret);
        await using var guest = new Side(TunnelRole.Guest, sessionId, secret);
        using var wrong = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));

        var timeout = TimeSpan.FromSeconds(3);
        var hostTask = host.Connector.ConnectAsync(new PeerEndpointInfo(wrong.FingerprintHex, guest.Candidates), timeout, CancellationToken.None);
        var guestTask = guest.Connector.ConnectAsync(new PeerEndpointInfo(wrong.FingerprintHex, host.Candidates), timeout, CancellationToken.None);
        var hostOutcome = await hostTask;
        var guestOutcome = await guestTask;

        Assert.False(hostOutcome.Result.Connected);
        Assert.False(guestOutcome.Result.Connected);
    }

    [Fact]
    public async Task ExternalCancellation_ReportsCancelled()
    {
        await using var host = new Side(TunnelRole.Host, Guid.NewGuid(), TestMaterial.NewSecret());
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var peer = new PeerEndpointInfo(peerCert.FingerprintHex, Array.Empty<CandidateEndpoint>());
        using var cts = new CancellationTokenSource(300);

        var outcome = await host.Connector.ConnectAsync(peer, TimeSpan.FromSeconds(10), cts.Token);
        Assert.False(outcome.Result.Connected);
        Assert.Equal("cancelled", outcome.Result.FailureReason);
    }

    [Fact]
    public async Task ConnectAsync_IsSingleUse()
    {
        await using var host = new Side(TunnelRole.Host, Guid.NewGuid(), TestMaterial.NewSecret());
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var peer = new PeerEndpointInfo(peerCert.FingerprintHex, Array.Empty<CandidateEndpoint>());
        await host.Connector.ConnectAsync(peer, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Connector.ConnectAsync(peer, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidPeerFingerprint_IsRejected()
    {
        await using var host = new Side(TunnelRole.Host, Guid.NewGuid(), TestMaterial.NewSecret());
        var peer = new PeerEndpointInfo("not-hex", Array.Empty<CandidateEndpoint>());
        await Assert.ThrowsAsync<ArgumentException>(() => host.Connector.ConnectAsync(peer, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private static List<Dictionary<string, object?>> Rows(SymmetricConnectOutcome outcome)
        => Assert.IsType<List<Dictionary<string, object?>>>(outcome.Diagnostics["candidates"]);

    private static string Describe(SymmetricConnectOutcome outcome)
        => System.Text.Json.JsonSerializer.Serialize(outcome.Diagnostics);

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64)
        {
            var n = await stream.ReadAsync(one, cts.Token);
            if (n == 0) throw new EndOfStreamException();
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
