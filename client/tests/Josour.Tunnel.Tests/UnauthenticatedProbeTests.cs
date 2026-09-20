using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Certificates;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests;

/// <summary>
/// Counting the unauthorised access attempts on the tunnel's listener and reporting them (<c>docs/protocol.md</c> section 2 with
/// <c>docs/api.md</c>: <c>POST /diagnostics</c> and the reserved keys). The listener is open between <c>session.created</c>
/// and <c>session.connected</c> only, and it is the one place where the system sees a connection that does not pass <c>AUTH1</c>;
/// none of it passes through the server, so the server cannot observe it.
/// </summary>
public class UnauthenticatedProbeTests
{
    // ---------- The log itself ----------

    [Fact]
    public void Log_CountsEveryAttempt_AndDeduplicatesAddresses()
    {
        var log = new UnauthenticatedProbeLog();
        for (var i = 0; i < 5; i++) log.Record(IPAddress.Parse("198.51.100.7"));
        log.Record(IPAddress.Parse("198.51.100.8"));

        Assert.Equal(6, log.Count);
        Assert.Equal(2, log.DistinctPeers);
        Assert.Equal(new[] { "198.51.100.7", "198.51.100.8" }, log.Peers);
    }

    /// <summary>The cap is ten addresses (<c>docs/api.md</c>), but the total count and the distinct count stay correct above it.</summary>
    [Fact]
    public void Log_CapsTheAddressListAtTen_ButKeepsCounting()
    {
        var log = new UnauthenticatedProbeLog();
        for (var i = 1; i <= 40; i++) log.Record(IPAddress.Parse($"203.0.113.{i}"));

        Assert.Equal(40, log.Count);
        Assert.Equal(40, log.DistinctPeers);
        Assert.Equal(UnauthenticatedProbeLog.MaxPeers, log.Peers.Count);
        Assert.Equal("203.0.113.1", log.Peers[0]);   // in order of appearance, not at random
        Assert.Equal("203.0.113.10", log.Peers[9]);
    }

    /// <summary>The mapped address <c>::ffff:a.b.c.d</c> is the same as <c>a.b.c.d</c>: a DualMode listener sees it in both forms.</summary>
    [Fact]
    public void Log_NormalisesIpv4MappedAddresses()
    {
        var log = new UnauthenticatedProbeLog();
        log.Record(IPAddress.Parse("::ffff:198.51.100.9"));
        log.Record(IPAddress.Parse("198.51.100.9"));

        Assert.Equal(2, log.Count);
        Assert.Equal(1, log.DistinctPeers);
        Assert.Equal(new[] { "198.51.100.9" }, log.Peers);
    }

    [Fact]
    public void Log_WithoutARemoteAddress_StillCounts()
    {
        var log = new UnauthenticatedProbeLog();
        log.Record(null);
        Assert.Equal(1, log.Count);
        Assert.Empty(log.Peers);
    }

    /// <summary>The shape of <c>data</c> with the reserved keys literally, and <c>null</c> when there is nothing to report.</summary>
    [Fact]
    public void Log_ProducesTheReservedApiKeys()
    {
        var log = new UnauthenticatedProbeLog();
        Assert.Null(log.ToDiagnostics(4242));

        log.Record(IPAddress.Parse("198.51.100.1"));
        log.Record(IPAddress.Parse("198.51.100.2"));
        var data = log.ToDiagnostics(4242)!;

        Assert.Equal(2, data["listener_unauthenticated"]);
        Assert.Equal(4242, data["listener_port"]);
        Assert.Equal(new[] { "198.51.100.1", "198.51.100.2" }, Assert.IsType<List<string>>(data["unauthenticated_peers"]));
    }

    // ---------- The listener ----------

    /// <summary>What the listener refuses itself (beyond four pending connections) was closed with not a byte read: unauthenticated by definition.</summary>
    [Fact]
    public async Task Listener_RecordsConnectionsItRejectsOverCapacity()
    {
        await using var listener = new TunnelListener(0, IPAddress.Loopback);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await listener.StartAsync(async (socket, ct) =>
        {
            try { await Task.WhenAny(release.Task, Task.Delay(System.Threading.Timeout.Infinite, ct)); }
            finally { socket.Dispose(); }
        }, CancellationToken.None);

        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, listener.Port);
                    clients.Add(client);
                }
                catch (SocketException) { client.Dispose(); }
            }

            await Wait.UntilAsync(() => listener.RejectedOverCapacity > 0, TimeSpan.FromSeconds(5));
            Assert.True(listener.Probes.Count >= listener.RejectedOverCapacity,
                $"{listener.RejectedOverCapacity} connections were rejected over capacity but only {listener.Probes.Count} were recorded");
            Assert.Contains("127.0.0.1", listener.Probes.Peers);
        }
        finally
        {
            release.SetResult();
            foreach (var client in clients) client.Dispose();
            await listener.StopAsync();
        }
    }

    /// <summary>The log stays readable after the listener is closed: the cleanup (section 7) precedes reporting.</summary>
    [Fact]
    public async Task Listener_ProbesSurviveDisposal()
    {
        var listener = new TunnelListener(0, IPAddress.Loopback);
        listener.Probes.Record(IPAddress.Parse("198.51.100.5"));
        await listener.DisposeAsync();

        Assert.Equal(1, listener.Probes.Count);
        Assert.Equal(new[] { "198.51.100.5" }, listener.Probes.Peers);
    }

    // ---------- The symmetric connector ----------

    private sealed class HostSide : IAsyncDisposable
    {
        public HostSide()
        {
            Material = TestMaterial.Create(TunnelRole.Host);
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

        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            Certificate.Dispose();
        }
    }

    /// <summary>
    /// A port scanner that opens TCP and writes nonsense (no TLS and no AUTH1) during the connect window: it is counted as an unauthorised attempt,
    /// and it appears in the connection's diagnostics under <c>inbound_unauthenticated</c>.
    /// </summary>
    [Fact]
    public async Task Connector_RecordsAPortScannerThatNeverPassesAuth1()
    {
        await using var host = new HostSide();
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var unreachablePeer = new PeerEndpointInfo(peerCert.FingerprintHex, new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", Loopback.ClosedPort()) });

        // A long connect window the test cuts short as soon as the attempts are recorded, rather than waiting out the whole timeout for nothing.
        using var window = new CancellationTokenSource();
        var connect = host.Connector.ConnectAsync(unreachablePeer, TimeSpan.FromSeconds(30), window.Token);

        // Three "scanners": they open the socket, write bytes that are not a ClientHello, then close.
        for (var i = 0; i < 3; i++)
        {
            using var scanner = new TcpClient();
            await scanner.ConnectAsync(IPAddress.Loopback, host.Listener.Port);
            await scanner.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        }

        Assert.True(await Wait.UntilAsync(() => host.Listener.Probes.Count >= 3, TimeSpan.FromSeconds(15)),
            $"only {host.Listener.Probes.Count} of 3 port scans were recorded");
        window.Cancel();
        var outcome = await connect;

        Assert.False(outcome.Result.Connected);
        Assert.Equal(new[] { "127.0.0.1" }, host.Listener.Probes.Peers);
        Assert.Equal(host.Listener.Probes.Count, outcome.Diagnostics["inbound_unauthenticated"]);
    }

    /// <summary>
    /// A legitimate connection is not counted as an attempt. This test was flaky and exposed a real defect rather than a fragile test: the symmetric
    /// connection opens <b>two connections</b> between the two machines, one of which loses in every successful session, and section 2 step 5 requires closing it
    /// <b>with no reply</b> — so the losing side recorded it as an unauthorised attempt, that is, a false security signal in every healthy session.
    /// The race is repeated several times because the winner's order is not deterministic.
    /// </summary>
    [Fact]
    public async Task Connector_DoesNotRecordTheLegitimatePeer()
    {
        for (var round = 0; round < 6; round++) await OneLegitimateSessionAsync();
    }

    private static async Task OneLegitimateSessionAsync()
    {
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        var hostMaterial = TestMaterial.Create(TunnelRole.Host, sessionId, secret);
        using var hostCert = SessionCertificate.Create(hostMaterial.ExpiresAt);
        await using var hostListener = new TunnelListener(0, IPAddress.Loopback);
        var hostCandidates = new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", hostListener.Port) };
        var hostConnector = new SymmetricConnector(hostMaterial, hostCert, hostListener, new DirectTransport(), hostCandidates);

        var guestMaterial = TestMaterial.Create(TunnelRole.Guest, sessionId, secret);
        using var guestCert = SessionCertificate.Create(guestMaterial.ExpiresAt);
        await using var guestListener = new TunnelListener(0, IPAddress.Loopback);
        var guestCandidates = new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", guestListener.Port) };
        var guestConnector = new SymmetricConnector(guestMaterial, guestCert, guestListener, new DirectTransport(), guestCandidates);

        var timeout = TimeSpan.FromSeconds(15);
        var hostTask = hostConnector.ConnectAsync(new PeerEndpointInfo(guestCert.FingerprintHex, guestCandidates), timeout, CancellationToken.None);
        var guestTask = guestConnector.ConnectAsync(new PeerEndpointInfo(hostCert.FingerprintHex, hostCandidates), timeout, CancellationToken.None);
        var hostOutcome = await hostTask;
        var guestOutcome = await guestTask;

        Assert.True(hostOutcome.Result.Connected);
        Assert.True(guestOutcome.Result.Connected);
        Assert.Equal(0, hostListener.Probes.Count);
        Assert.Equal(0, guestListener.Probes.Count);
        Assert.Equal(0, hostOutcome.Diagnostics["inbound_unauthenticated"]);
        Assert.Equal(0, guestOutcome.Diagnostics["inbound_unauthenticated"]);

        if (hostOutcome.Connection is not null) await hostOutcome.Connection.DisposeAsync();
        if (guestOutcome.Connection is not null) await guestOutcome.Connection.DisposeAsync();
    }

    /// <summary>
    /// The inbound-connection rows in the diagnostics are bounded by a ceiling. With no ceiling the list grows with whatever any party on the network opens,
    /// so memory grows and <c>session.connect_failed</c> exceeds the 64 KB limit in <c>docs/api.md</c>, so the whole diagnostic is refused.
    /// </summary>
    [Fact]
    public async Task Connector_CapsTheRecordedInboundRows_ButKeepsCounting()
    {
        await using var host = new HostSide();
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var unreachablePeer = new PeerEndpointInfo(peerCert.FingerprintHex, new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", Loopback.ClosedPort()) });

        using var window = new CancellationTokenSource();
        var connect = host.Connector.ConnectAsync(unreachablePeer, TimeSpan.FromSeconds(30), window.Token);
        const int probes = SymmetricConnector.MaxRecordedInboundAttempts * 3;
        for (var i = 0; i < probes; i++)
        {
            using var scanner = new TcpClient();
            try
            {
                await scanner.ConnectAsync(IPAddress.Loopback, host.Listener.Port);
                await scanner.GetStream().WriteAsync(new byte[] { 0x00 });
            }
            catch (SocketException) { /* refused over capacity */ }
            catch (IOException) { /* closed */ }
        }

        Assert.True(await Wait.UntilAsync(
            () => host.Listener.InboundAttempts - host.Listener.RejectedOverCapacity > SymmetricConnector.MaxRecordedInboundAttempts,
            TimeSpan.FromSeconds(20)), "the probe loop never produced enough handled inbound attempts to engage the cap");
        window.Cancel();
        var outcome = await connect;

        var rows = (List<Dictionary<string, object?>>)outcome.Diagnostics["candidates"]!;
        var inboundRows = rows.Count(r => (string?)r["direction"] == "inbound");
        var dropped = (int)outcome.Diagnostics["inbound_dropped"]!;
        var handled = host.Listener.InboundAttempts - host.Listener.RejectedOverCapacity;

        Assert.True(inboundRows <= SymmetricConnector.MaxRecordedInboundAttempts,
            $"{inboundRows} inbound rows were kept, above the {SymmetricConnector.MaxRecordedInboundAttempts} cap");
        Assert.True(dropped > 0, "the cap never engaged");
        // An exact invariant: every inbound connection that reached the handler is counted once — either as a stored row or as a dropped one.
        Assert.Equal(handled, inboundRows + dropped);
        // The counter and the addresses stay bounded however large the number grows.
        Assert.True(host.Listener.Probes.Count > 0);
        Assert.True(host.Listener.Probes.Peers.Count <= UnauthenticatedProbeLog.MaxPeers);
    }

    // ---------- What track C reads ----------

    /// <summary>
    /// The three keys on <c>ITunnelSession.Diagnostics</c> under their names in <c>docs/api.md</c>, literally. This is the contract
    /// with track C: it reads them as they are and puts them in <c>data</c> with no renaming and no arithmetic.
    /// </summary>
    [Fact]
    public async Task Session_PublishesTheReservedDiagnosticsKeys()
    {
        var material = TestMaterial.Create(TunnelRole.Host);
        var session = new TunnelSession(material, new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableUpnp = false,
            CandidateSource = () => new StaticCandidateSource(),
            HostEgress = _ => new FakeEgress(),
        });

        await session.PrepareAsync(CancellationToken.None);
        var port = session.ListenPort;

        using (var scanner = new TcpClient())
        {
            await scanner.ConnectAsync(IPAddress.Loopback, port);
            await scanner.GetStream().WriteAsync(Encoding.ASCII.GetBytes("probe"));
        }

        await session.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);
        var diagnostics = session.Diagnostics;

        Assert.True(diagnostics.ContainsKey("listener_unauthenticated"), "listener_unauthenticated is missing from ITunnelSession.Diagnostics");
        Assert.True(diagnostics.ContainsKey("listener_port"), "listener_port is missing from ITunnelSession.Diagnostics");
        Assert.True(diagnostics.ContainsKey("unauthenticated_peers"), "unauthenticated_peers is missing from ITunnelSession.Diagnostics");
        Assert.Equal(port, diagnostics["listener_port"]);
        Assert.IsType<int>(diagnostics["listener_unauthenticated"]);
        var peers = Assert.IsType<List<string>>(diagnostics["unauthenticated_peers"]);
        Assert.True(peers.Count <= UnauthenticatedProbeLog.MaxPeers);
    }

    /// <summary>A session with no attempts publishes the keys with a zero: the zero is the rate's denominator at the server, not the key's absence.</summary>
    [Fact]
    public async Task Session_PublishesZero_WhenNothingProbedTheListener()
    {
        var material = TestMaterial.Create(TunnelRole.Host);
        var session = new TunnelSession(material, new TunnelSessionOptions
        {
            BindAddress = IPAddress.Loopback,
            EnableUpnp = false,
            CandidateSource = () => new StaticCandidateSource(),
            HostEgress = _ => new FakeEgress(),
        });

        await session.PrepareAsync(CancellationToken.None);
        await session.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);

        Assert.Equal(0, session.Diagnostics["listener_unauthenticated"]);
        Assert.Empty(Assert.IsType<List<string>>(session.Diagnostics["unauthenticated_peers"]));
    }
}
