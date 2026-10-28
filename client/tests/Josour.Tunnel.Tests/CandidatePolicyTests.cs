using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Certificates;

namespace Josour.Tunnel.Tests;

/// <summary>
/// The other side's candidates come from the peer through the server, so we refuse to dial the ones that can only point us at
/// ourselves or at nobody: loopback (outside the in-process setups), multicast, broadcast and unspecified addresses.
/// </summary>
public class CandidatePolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.10.20.30", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("0.1.2.3", "unspecified")]
    [InlineData("::", "unspecified")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.250", "multicast")]           // SSDP
    [InlineData("ff02::1", "multicast")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("240.0.0.1", "broadcast")]
    [InlineData("example.com", "not_an_ip")]
    public void Refused(string ip, string reason)
    {
        Assert.Equal(reason, CandidatePolicy.RefusalReason(ip, 4000, allowLoopback: false));
    }

    [Theory]
    [InlineData("192.168.1.20")]      // a peer on the same LAN legitimately offers these
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.7")]
    [InlineData("2a02:1234::5")]
    [InlineData("fe80::1")]
    public void Dialable(string ip)
    {
        Assert.Null(CandidatePolicy.RefusalReason(ip, 4000, allowLoopback: false));
    }

    [Fact]
    public void Loopback_OnlyInTheInProcessSetup()
    {
        Assert.Null(CandidatePolicy.RefusalReason("127.0.0.1", 4000, allowLoopback: true));
        Assert.Null(CandidatePolicy.RefusalReason("::1", 4000, allowLoopback: true));
        // Even then multicast and unspecified stay refused.
        Assert.Equal("unspecified", CandidatePolicy.RefusalReason("0.0.0.0", 4000, allowLoopback: true));
        Assert.Equal("multicast", CandidatePolicy.RefusalReason("224.0.0.1", 4000, allowLoopback: true));
        Assert.Equal("bad_port", CandidatePolicy.RefusalReason("192.168.1.20", 0, allowLoopback: true));
    }

    [Fact]
    public async Task Connector_NeverDialsARefusedCandidate()
    {
        var material = TestMaterial.Create(TunnelRole.Host);
        using var certificate = SessionCertificate.Create(material.ExpiresAt);
        await using var listener = new TunnelListener(0, IPAddress.Loopback);
        var transport = new RecordingTransport();
        var connector = new SymmetricConnector(material, certificate, listener, transport);
        using var peerCertificate = SessionCertificate.Create(material.ExpiresAt);
        var peer = new PeerEndpointInfo(peerCertificate.FingerprintHex, new[]
        {
            new CandidateEndpoint(CandidateType.Lan, "0.0.0.0", 4000),
            new CandidateEndpoint(CandidateType.Public, "224.0.0.251", 5353),
            new CandidateEndpoint(CandidateType.Public, "255.255.255.255", 4000),
            new CandidateEndpoint(CandidateType.V6, "::", 4000),
            new CandidateEndpoint(CandidateType.Lan, "192.0.2.10", 4000),
        });

        var outcome = await connector.ConnectAsync(peer, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.False(outcome.Result.Connected);
        Assert.Equal(new[] { "192.0.2.10" }, transport.Dialled.ToArray());
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(outcome.Diagnostics["candidates"]);
        Assert.Equal(4, rows.Count(r => r["error"] is string e && e.StartsWith("refused: ", StringComparison.Ordinal)));
    }

    private sealed class RecordingTransport : ITunnelTransport
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Dialled { get; } = new();
        public string Name => "recording";

        public Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
        {
            Dialled.Enqueue(endpoint.Ip);
            return Task.FromException<Stream>(new IOException("not reachable in this test"));
        }
    }
}
