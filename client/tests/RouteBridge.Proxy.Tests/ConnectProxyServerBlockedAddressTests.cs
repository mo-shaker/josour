using System.Net;
using System.Net.Sockets;

using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Proxy.Tests;

/// <summary>
/// المسار المباشر على جانب الضيف بسياسة العناوين الحقيقية (بلا استثناء loopback الذي تستعمله بقية الاختبارات).
///
/// <para>
/// لماذا يهم: <see cref="ProxyRouter"/> يرفض العنوان الحرفي، لكن الاسم الذي <b>يحلّ</b> إلى عنوان داخلي كان يمر
/// إلى <c>Socket.ConnectAsync</c> قبل تقوية الأسبوع 4. الـ Proxy يسمع على 127.0.0.1 ويقبل من أي عملية محلية
/// (فحص المالك يعمل فقط حين يُمرَّر متصفح)، فكان يصلح جسرًا نحو ما يسمعه هذا الجهاز نفسه: مستمع النفق أثناء
/// نافذة الاتصال، منفذ الـ Relay، وأي خدمة على 127.0.0.1. الآن يُرفض بعد الحل وقبل أي مقبس.
/// </para>
/// </summary>
public class ConnectProxyServerBlockedAddressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>مستمع يمثل ما يسمعه هذا الجهاز نفسه (مستمع النفق أو منفذ Relay محلي). يجب ألا يقبل شيئًا أبدًا.</summary>
    private sealed class Victim : IDisposable
    {
        private readonly TcpListener _listener;
        private int _accepted;

        public Victim()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var socket = await _listener.AcceptSocketAsync();
                        Interlocked.Increment(ref _accepted);
                    }
                }
                catch { /* أُوقف */ }
            });
        }

        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public void Dispose() => _listener.Stop();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.7.7")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:0001::")]
    [InlineData("::127.0.0.1")]
    public async Task Connect_NameResolvingToABlockedAddress_Is403_AfterResolution_WithNoConnection(string resolvesTo)
    {
        using var victim = new Victim();
        var resolver = new StubResolver().Map("evil.test", resolvesTo);
        var mux = new FakeMux();
        // سياسة العناوين الحقيقية: لا استثناء loopback هنا.
        await using var proxy = Proxies.Start(mux, resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        // ‏evil.test ليس في القائمة ⇒ المسار المباشر، وهو المسار الذي نختبره.
        await client.SendAsync($"CONNECT evil.test:{victim.Port} HTTP/1.1\r\nHost: evil.test:{victim.Port}\r\n\r\n");
        var head = await client.ReadHeadAsync();

        Assert.Equal(403, head.Status);
        Assert.Equal(1, resolver.Calls);                          // الرفض وقع بعد الحل لا قبله
        Assert.Equal(0, victim.Accepted);                         // ولم يُفتح أي مقبس نحو الهدف
        Assert.Empty(mux.Opens);                                  // ولم يُطلب من المضيف أن يفتحه نيابةً عنا
        Assert.Equal(1, proxy.Counters.Rejected);
        Assert.Equal(0, proxy.Counters.DirectConnects);
        Assert.True(await client.ClosedWithoutDataAsync());
    }

    [Fact]
    public async Task Connect_AnyBlockedAddressInTheAnswerRejectsAll_EvenWhenAPublicOneIsAlsoThere()
    {
        // DNS يعيد عنوانًا عامًا وآخر داخليًا: قاعدة العقد "أي عنوان محظور ⇒ ارفض" (القسم 6 الخطوة 6)
        // تمنع Happy Eyeballs من اختيار الداخلي بعد الفحص.
        using var victim = new Victim();
        var resolver = new StubResolver().Map("mixed.test", "93.184.216.34", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT mixed.test:{victim.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, victim.Accepted);
    }

    [Fact]
    public async Task Http_PlainRequestToABlockedName_Is403_WithNoConnection()
    {
        // نفس القاعدة على مسار http:// المباشر لا على CONNECT وحده.
        using var victim = new Victim();
        var resolver = new StubResolver().Map("evil.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"GET http://evil.test:{victim.Port}/x HTTP/1.1\r\nHost: evil.test:{victim.Port}\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, victim.Accepted);
        Assert.Equal(0, proxy.Counters.DirectHttpRequests);
    }

    [Fact]
    public async Task Connect_ToTheProxysOwnPort_IsRejected_SoItCannotLoopBackIntoItself()
    {
        var resolver = new StubResolver().Map("loop.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT loop.test:{proxy.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(1, proxy.Counters.Accepted); // اتصال واحد فقط: لم يتصل الـ Proxy بنفسه
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.1")]
    [InlineData("[::ffff:169.254.169.254]")]
    [InlineData("[64:ff9b::a00:1]")]
    public async Task Connect_ToAnIpLiteral_IsRejectedBeforeAnyResolution(string literal)
    {
        var resolver = new StubResolver();
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT {literal}:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, resolver.Calls); // العنوان الحرفي يسقط في الخطوة الأولى: لا DNS ولا مقبس
    }

    [Fact]
    public async Task Connect_ToAPublicAddress_StillWorks_UnderTheRealPolicy()
    {
        // إثبات أن التقوية لم تغلق المسار المباشر كله: عنوان عام يُوصل إليه كالمعتاد.
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        var resolver = new StubResolver().Map("public.test", "127.0.0.1");
        // نُبقي السياسة الحقيقية عدا loopback لأنه لا سبيل لأصل عام حقيقي داخل الاختبار.
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.BlockedExceptLoopback);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT public.test:{origin.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        using var accepted = await accept.WaitAsync(Timeout);
        Assert.Equal(1, proxy.Counters.DirectConnects);
    }
}
