using System.Security.Cryptography;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Auth;

namespace Josour.Tunnel.Tests;

public class AuthHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] ListenerFp = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task Success_BothSidesComplete()
    {
        var secret = TestMaterial.NewSecret();
        var id = Guid.NewGuid();
        var guest = TestMaterial.Create(TunnelRole.Guest, id, secret);
        var host = TestMaterial.Create(TunnelRole.Host, id, secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);
        var guestTask = AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, ListenerFp, Timeout, CancellationToken.None);
        await hostTask;
        await guestTask;
    }

    [Fact]
    public async Task WrongSecret_HostRejects_AndWritesNothing()
    {
        var id = Guid.NewGuid();
        var guest = TestMaterial.Create(TunnelRole.Guest, id, TestMaterial.NewSecret());
        var host = TestMaterial.Create(TunnelRole.Host, id, TestMaterial.NewSecret());
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);
        var guestTask = AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, ListenerFp, Timeout, CancellationToken.None);

        await Assert.ThrowsAsync<AuthFailedException>(() => hostTask);
        await StreamAssert.NothingReadableAsync(a, TimeSpan.FromMilliseconds(200));

        // المستدعي يغلق الاتصال؛ Guest يرى الإغلاق قبل AUTH2
        await b.DisposeAsync();
        await Assert.ThrowsAsync<AuthFailedException>(() => guestTask);
    }

    [Fact]
    public async Task WrongListenerFingerprint_Fails()
    {
        var secret = TestMaterial.NewSecret();
        var id = Guid.NewGuid();
        var guest = TestMaterial.Create(TunnelRole.Guest, id, secret);
        var host = TestMaterial.Create(TunnelRole.Host, id, secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);
        var guestTask = AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, RandomNumberGenerator.GetBytes(32), Timeout, CancellationToken.None);

        await Assert.ThrowsAsync<AuthFailedException>(() => hostTask);
        await b.DisposeAsync();
        await Assert.ThrowsAsync<AuthFailedException>(() => guestTask);
    }

    [Fact]
    public async Task WrongSessionId_Fails()
    {
        var secret = TestMaterial.NewSecret();
        var guest = TestMaterial.Create(TunnelRole.Guest, Guid.NewGuid(), secret);
        var host = TestMaterial.Create(TunnelRole.Host, Guid.NewGuid(), secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);
        _ = AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, ListenerFp, Timeout, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        await Assert.ThrowsAsync<AuthFailedException>(() => hostTask);
    }

    [Fact]
    public async Task Host_WritesNothing_BeforeAuth1()
    {
        var host = TestMaterial.Create(TunnelRole.Host);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;
        using var cts = new CancellationTokenSource();

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, cts.Token);
        await StreamAssert.NothingReadableAsync(a, TimeSpan.FromMilliseconds(200));
        Assert.False(hostTask.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostTask);
    }

    [Fact]
    public async Task Guest_TimesOut_WhenHostIsSilent()
    {
        var guest = TestMaterial.Create(TunnelRole.Guest);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, ListenerFp, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task Host_TimesOut_WhenGuestIsSilent()
    {
        var host = TestMaterial.Create(TunnelRole.Host);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task WireFormat_MatchesProtocolSection4()
    {
        // يتحقق من الصيغة بحساب مستقل: 81 بايت، version=1، session_id big-endian (RFC 4122)، mac = HMAC-SHA256 على label||sid||random||fp.
        var secret = TestMaterial.NewSecret();
        var id = Guid.NewGuid();
        var guest = TestMaterial.Create(TunnelRole.Guest, id, secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var guestTask = AuthHandshake.SendAuth1AndVerifyAuth2Async(a, guest, ListenerFp, Timeout, CancellationToken.None);

        var auth1 = new byte[81];
        await b.ReadExactlyAsync(auth1);
        await StreamAssert.NothingReadableAsync(b, TimeSpan.FromMilliseconds(100)); // لا بايت زائد

        Assert.Equal(1, auth1[0]);
        var expectedSessionId = Convert.FromHexString(id.ToString("N")); // الصيغة النصية للـ GUID هي big-endian
        Assert.Equal(expectedSessionId, auth1[1..17]);
        var clientRandom = auth1[17..49];
        var mac1 = auth1[49..81];
        Assert.Equal(Mac("rb-auth1", secret, expectedSessionId, clientRandom, ListenerFp), mac1);

        var mac2 = Mac("rb-auth2", secret, expectedSessionId, clientRandom, ListenerFp);
        await b.WriteAsync(mac2);
        await b.FlushAsync();
        await guestTask;
    }

    [Fact]
    public async Task Host_AcceptsIndependentlyBuiltAuth1_AndAnswersCorrectAuth2()
    {
        var secret = TestMaterial.NewSecret();
        var id = Guid.NewGuid();
        var host = TestMaterial.Create(TunnelRole.Host, id, secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);

        var sid = Convert.FromHexString(id.ToString("N"));
        var random = RandomNumberGenerator.GetBytes(32);
        var auth1 = new byte[] { 1 }.Concat(sid).Concat(random).Concat(Mac("rb-auth1", secret, sid, random, ListenerFp)).ToArray();
        Assert.Equal(81, auth1.Length);
        await a.WriteAsync(auth1);
        await a.FlushAsync();

        var auth2 = new byte[32];
        await a.ReadExactlyAsync(auth2);
        Assert.Equal(Mac("rb-auth2", secret, sid, random, ListenerFp), auth2);
        await hostTask;
    }

    [Fact]
    public async Task Host_RejectsUnsupportedVersion()
    {
        var secret = TestMaterial.NewSecret();
        var id = Guid.NewGuid();
        var host = TestMaterial.Create(TunnelRole.Host, id, secret);
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;

        var hostTask = AuthHandshake.VerifyAuth1AndSendAuth2Async(b, host, ListenerFp, Timeout, CancellationToken.None);
        var sid = Convert.FromHexString(id.ToString("N"));
        var random = RandomNumberGenerator.GetBytes(32);
        var auth1 = new byte[] { 2 }.Concat(sid).Concat(random).Concat(Mac("rb-auth1", secret, sid, random, ListenerFp)).ToArray();
        await a.WriteAsync(auth1);
        await a.FlushAsync();

        await Assert.ThrowsAsync<AuthFailedException>(() => hostTask);
        await StreamAssert.NothingReadableAsync(a, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void SessionIdBytes_AreBigEndianRfc4122()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal(Convert.FromHexString("00112233445566778899aabbccddeeff"), AuthHandshake.SessionIdBytes(id));
    }

    [Fact]
    public async Task InvalidMaterial_IsRejectedUpFront()
    {
        var (a, b) = await Loopback.CreatePairAsync();
        await using var _a = a;
        await using var _b = b;
        var badSecret = TestMaterial.Create(TunnelRole.Guest, secret: new byte[16]);
        await Assert.ThrowsAsync<ArgumentException>(() => AuthHandshake.SendAuth1AndVerifyAuth2Async(a, badSecret, ListenerFp, Timeout, CancellationToken.None));
        var good = TestMaterial.Create(TunnelRole.Host);
        await Assert.ThrowsAsync<ArgumentException>(() => AuthHandshake.VerifyAuth1AndSendAuth2Async(b, good, new byte[31], Timeout, CancellationToken.None));
    }

    private static byte[] Mac(string label, byte[] secret, byte[] sid, byte[] random, byte[] fp)
    {
        var data = Encoding.ASCII.GetBytes(label).Concat(sid).Concat(random).Concat(fp).ToArray();
        return HMACSHA256.HashData(secret, data);
    }
}
