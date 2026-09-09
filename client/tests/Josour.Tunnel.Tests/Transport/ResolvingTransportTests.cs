using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests.Transport;

/// <summary>
/// الفرق بين النقلين فرق <b>مصدر العنوان</b> لا فرق تشدد، وهذه الاختبارات تثبّت الطرفين معًا.
///
/// <para>عنوان الـ Relay يأتي من خادمنا في <c>session.created.relay</c> وهو اسم مضيف بحكم التصميم؛ أما
/// المرشحون فيأتون من النظير ويشترط العقد أن يكونوا عناوين حرفية. توسيع <see cref="DirectTransport"/> ليقبل
/// الأسماء كان سيجعل النظير قادرًا على دفعنا لحلّ ما يختاره، فبقي صارمًا وأُضيف نقل ثانٍ.</para>
/// </summary>
public class ResolvingTransportTests
{
    private static async Task<(Socket Listener, int Port)> ListenAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        _ = Task.Run(async () =>
        {
            try { (await listener.AcceptAsync()).Dispose(); } catch { /* torn down */ }
        });
        await Task.Yield();
        return (listener, ((IPEndPoint)listener.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task DirectTransportStillRefusesAHostName()
    {
        // The guard that made the relay unreachable in production. It stays: candidates arrive
        // from the peer, and resolving a name it chose would be a capability we do not want.
        await Assert.ThrowsAsync<ArgumentException>(() => new DirectTransport().ConnectAsync(
            new CandidateEndpoint(CandidateType.Public, "localhost", 443), TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task ResolvingTransportConnectsToAHostName()
    {
        var (listener, port) = await ListenAsync();
        using (listener)
        {
            await using var stream = await new ResolvingTransport().ConnectAsync(
                new CandidateEndpoint(CandidateType.Relay, "localhost", port), TimeSpan.FromSeconds(10), default);
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public async Task ResolvingTransportTriesEveryAddressTheNameResolvesTo()
    {
        // "localhost" resolves to ::1 and 127.0.0.1 on a normal machine, and the listener above is
        // IPv4 only. Stopping at the first address would connect only when the order happened to
        // favour us, which is the kind of pass that turns into a field failure on another network.
        var addresses = await Dns.GetHostAddressesAsync("localhost");
        Assert.True(addresses.Length >= 1);

        var (listener, port) = await ListenAsync();
        using (listener)
        {
            await using var stream = await new ResolvingTransport().ConnectAsync(
                new CandidateEndpoint(CandidateType.Relay, "localhost", port), TimeSpan.FromSeconds(10), default);
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public async Task AnIpLiteralStillTakesTheDirectPath()
    {
        var (listener, port) = await ListenAsync();
        using (listener)
        {
            await using var stream = await new ResolvingTransport().ConnectAsync(
                new CandidateEndpoint(CandidateType.Relay, "127.0.0.1", port), TimeSpan.FromSeconds(10), default);
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public async Task AnUnresolvableNameFailsAsASocketErrorNotAnArgumentError()
    {
        // The caller treats a socket failure as "this path did not work" and an ArgumentException
        // as a bug in its own wiring; reporting the wrong one is what hid the original defect.
        await Assert.ThrowsAsync<SocketException>(() => new ResolvingTransport().ConnectAsync(
            new CandidateEndpoint(CandidateType.Relay, "no-such-host.josour.invalid", 443),
            TimeSpan.FromSeconds(10), default));
    }
}
