using System.Net;
using System.Net.Sockets;
using Josour.Core.Net;
using Josour.Proxy;

namespace Josour.E2E.Tests;

/// <summary>
/// «هل يستطيع الضيف أن يصل، عبر النفق، إلى ما يسمعه جهاز المضيف نفسه؟» — الجواب الذي تثبته هذه الاختبارات: لا،
/// والرفض يقع **بعد** حل الاسم و**قبل** أي مقبس.
///
/// <para>
/// النموذج التهديدي: الضيف يتحكم بالكامل في اسم الوجهة (يكتبه في CONNECT) وقد يتحكم بمنطقة DNS، فيستطيع أن يجعل
/// اسمًا في القائمة يحل إلى 127.0.0.1 أو إلى عنوان المضيف الداخلي. لو مرّ، لكان الضيف قد صار جسرًا إلى مستمع
/// النفق على المضيف، إلى منفذ الـ Relay، وإلى كل ما تسمعه شبكة المضيف. هذه أخطر حالة في المنتج كله.
/// </para>
///
/// <para>
/// خلافًا لبقية اختبارات E2E، سياسة العناوين هنا هي الإنتاجية بلا استثناء loopback: الضحية مستمع حقيقي على
/// 127.0.0.1 وعدّاد قبوله هو الدليل — صفر يعني أن أحدًا لم يتصل به.
/// </para>
/// </summary>
public class TunnelSelfAccessTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private Victim _victim = null!;
    private InProcessTunnelPair _pair = null!;

    /// <summary>مستمع على المضيف يمثل مستمع النفق أو منفذ الـ Relay أو أي خدمة داخلية. يجب ألا يقبل شيئًا.</summary>
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

    public async Task InitializeAsync()
    {
        _victim = new Victim();
        // ‏self.test و metadata.test في القائمة على الطرفين: القائمة ليست ما يحمينا هنا، بل سياسة العناوين.
        _pair = await InProcessTunnelPair.CreateAsync(
            extraEntries: new[] { $"self.test:{_victim.Port}", $"metadata.test:{_victim.Port}", $"mixed.test:{_victim.Port}" },
            hostAddressBlocker: a => IpRangePolicy.IsBlocked(a),
            guestAddressBlocker: a => IpRangePolicy.IsBlocked(a),
            extraHostNames: new Dictionary<string, string[]>
            {
                ["self.test"] = new[] { "127.0.0.1" },
                ["metadata.test"] = new[] { "169.254.169.254" },
                ["mixed.test"] = new[] { "93.184.216.34", "127.0.0.1" },
            });
    }

    public async Task DisposeAsync()
    {
        await _pair.DisposeAsync();
        _victim.Dispose();
    }

    [Theory]
    [InlineData("self.test")]      // loopback على جهاز المضيف: مستمع النفق ومنفذ الـ Relay يسكنان هنا
    [InlineData("metadata.test")]  // بيانات السحابة الوصفية
    [InlineData("mixed.test")]     // إجابة DNS تخلط عنوانًا عامًا بآخر داخلي
    public async Task Connect_ToANameResolvingToTheHostsOwnAddress_Is403_AndNothingIsEverConnected(string name)
    {
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT {name}:{_victim.Port} HTTP/1.1\r\nHost: {name}:{_victim.Port}\r\n\r\n"));

        var head = await Http.ReadHeadAsync(stream);

        // ‏OPEN_FAIL(private_ip) → 403 عند المتصفح (docs/protocol.md القسم 6 القاعدة 6 وجدول StatusForOpenFail).
        Assert.Equal(403, head.Status);
        Assert.Equal(0, _victim.Accepted);

        // المضيف حاول فعلًا (فُتح stream وفشل بالسياسة) ولم يُسجَّل النطاق كأنه زِيْر.
        Assert.Equal(0, _pair.HostHandler.OpensOk);
        Assert.True(_pair.HostHandler.OpensFailed >= 1);
        Assert.DoesNotContain(name, _pair.Host.DomainsSeen);

        // ولا بايت واحد عبر النفق نحو الوجهة.
        Assert.Equal(0, _pair.HostHandler.Counter.Up);
        Assert.Equal(0, _pair.HostHandler.Counter.Down);
    }

    [Fact]
    public async Task Connect_ToTheHostsOwnListener_StaysBlockedAcrossRepeatedAttempts()
    {
        // محاولة واحدة قد تفشل لسبب عابر؛ عشر محاولات متتالية تثبت أن الرفض سياسة لا صدفة.
        for (var i = 0; i < 10; i++)
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(0, _victim.Accepted);
        Assert.Equal(0, _pair.HostHandler.OpensOk);
    }

    [Fact]
    public async Task Connect_ToAnIpLiteral_NeverEvenReachesTheHost()
    {
        // العنوان الحرفي يسقط محليًا على الضيف (ProxyRouter): لا OPEN عبر النفق أصلًا.
        var before = _pair.HostHandler.OpensFailed;
        foreach (var literal in new[] { "127.0.0.1", "[::1]", "10.0.0.1", "[::ffff:127.0.0.1]" })
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT {literal}:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(before, _pair.HostHandler.OpensFailed);
        Assert.Equal(0, _victim.Accepted);
    }

    [Fact]
    public async Task Connect_ToTheGuestsOwnProxyPort_IsRejected()
    {
        // ‏self.test يحل إلى 127.0.0.1 على الضيف أيضًا: لا يصير الـ Proxy جسرًا إلى نفسه ولا إلى مستمع الضيف.
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_pair.ProxyPort} HTTP/1.1\r\n\r\n"));

        // المنفذ ليس ضمن قيد المدخل (self.test:<victim>) ⇒ ليس عبر النفق ⇒ المسار المباشر ⇒ حظر بعد الحل.
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        Assert.Equal(0, _victim.Accepted);
    }

    [Fact]
    public async Task UnderTheProductionPolicy_EvenTheAllowlistedOriginIsRefused_BecauseItTooIsLoopbackHere()
    {
        // ضابط صريح: <c>site.test</c> في القائمة ويعمل في بقية اختبارات E2E — لأنها تستثني loopback.
        // هنا السياسة إنتاجية، فيُرفض هو الآخر. المقصد: الرفض أعلاه سببه العنوان لا القائمة ولا عطب في الاتصال.
        var serve = _pair.Origin.ServeOnceAsync("never served");
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{_pair.OriginPort} HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        Assert.False(serve.IsCompleted);
    }

    [Fact]
    public async Task TheTunnelItselfIsStillAlive_AfterEveryRejection()
    {
        // الرفض بالسياسة لا يقتل النفق: الجلستان تبقيان Connected والـ Mux مفتوح ويقبل stream آخر.
        for (var i = 0; i < 3; i++)
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(Josour.Core.Tunnel.TunnelState.Connected, _pair.Host.State);
        Assert.Equal(Josour.Core.Tunnel.TunnelState.Connected, _pair.Guest.State);
        Assert.False(_pair.Host.MuxClosed);
        Assert.False(_pair.Guest.MuxClosed);
        _ = Timeout;
    }
}
