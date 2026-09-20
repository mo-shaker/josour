using System.Security.Authentication;
using Josour.Tunnel.Certificates;
using Josour.Tunnel.Tls;

namespace Josour.Tunnel.Tests;

public class TlsChannelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PinnedFingerprint_HandshakeSucceeds_WithTls12OrNewer()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (client, server) = await Loopback.CreatePairAsync();

        var serverTask = TlsChannel.AuthenticateAsServerAsync(server, cert.Certificate, Timeout, CancellationToken.None);
        var clientTask = TlsChannel.AuthenticateAsClientAsync(client, cert.FingerprintSha256, Timeout, CancellationToken.None);
        var serverResult = await serverTask;
        var clientResult = await clientTask;

        Assert.Contains(serverResult.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(serverResult.TlsVersion, clientResult.TlsVersion);
        Assert.True(TlsChannel.IsAtLeastTls12(serverResult.Protocol));

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await clientResult.Stream.WriteAsync(payload);
        await clientResult.Stream.FlushAsync();
        var received = new byte[5];
        await serverResult.Stream.ReadExactlyAsync(received);
        Assert.Equal(payload, received);

        await serverResult.Stream.DisposeAsync();
        await clientResult.Stream.DisposeAsync();
    }

    [Fact]
    public async Task WrongFingerprint_ClientRejectsCertificate()
    {
        using var serverCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        using var otherCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (client, server) = await Loopback.CreatePairAsync();

        var serverTask = TlsChannel.AuthenticateAsServerAsync(server, serverCert.Certificate, Timeout, CancellationToken.None);
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            TlsChannel.AuthenticateAsClientAsync(client, otherCert.FingerprintSha256, Timeout, CancellationToken.None));

        try { var r = await serverTask; await r.Stream.DisposeAsync(); }
        catch (Exception) { /* the server fails too, or notices the close; both are acceptable */ }
    }

    [Fact]
    public async Task ServerHandshake_TimesOut_WhenClientIsSilent()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (client, server) = await Loopback.CreatePairAsync();
        await using var _ = client;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            TlsChannel.AuthenticateAsServerAsync(server, cert.Certificate, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task ClientHandshake_TimesOut_WhenServerIsSilent()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (client, server) = await Loopback.CreatePairAsync();
        await using var _ = server;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            TlsChannel.AuthenticateAsClientAsync(client, cert.FingerprintSha256, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task ClientHandshake_RejectsFingerprintOfWrongLength()
    {
        var (client, server) = await Loopback.CreatePairAsync();
        await using var _ = server;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TlsChannel.AuthenticateAsClientAsync(client, new byte[16], Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task ExternalCancellation_PropagatesAsCancellation()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (client, server) = await Loopback.CreatePairAsync();
        await using var _ = client;
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TlsChannel.AuthenticateAsServerAsync(server, cert.Certificate, Timeout, cts.Token));
    }
}
