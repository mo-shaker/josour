using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.Tunnel.Tests;

/// <summary>
/// عدّ محاولات الوصول غير المصرَّح بها على مستمع النفق ورفعها (‏<c>docs/protocol.md</c> القسم 2 مع
/// <c>docs/api.md</c>: <c>POST /diagnostics</c> والمفاتيح المحجوزة). المستمع مفتوح بين <c>session.created</c>
/// و<c>session.connected</c> فقط، وهو الموضع الوحيد الذي يرى فيه النظام اتصالًا لا يجتاز <c>AUTH1</c>؛
/// الخادم لا يمر به شيء منه فلا يستطيع رصده.
/// </summary>
public class UnauthenticatedProbeTests
{
    // ---------- السجل نفسه ----------

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

    /// <summary>الحد عشرة عناوين (‏<c>docs/api.md</c>)، لكن العدّ الكلي وعدد المميزة يبقيان صحيحين فوقه.</summary>
    [Fact]
    public void Log_CapsTheAddressListAtTen_ButKeepsCounting()
    {
        var log = new UnauthenticatedProbeLog();
        for (var i = 1; i <= 40; i++) log.Record(IPAddress.Parse($"203.0.113.{i}"));

        Assert.Equal(40, log.Count);
        Assert.Equal(40, log.DistinctPeers);
        Assert.Equal(UnauthenticatedProbeLog.MaxPeers, log.Peers.Count);
        Assert.Equal("203.0.113.1", log.Peers[0]);   // بترتيب الظهور، لا عشوائيًا
        Assert.Equal("203.0.113.10", log.Peers[9]);
    }

    /// <summary>العنوان المخطَّط <c>::ffff:a.b.c.d</c> هو نفسه <c>a.b.c.d</c>: مستمع DualMode يراه بالشكلين.</summary>
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

    /// <summary>شكل <c>data</c> بالمفاتيح المحجوزة حرفيًا، و<c>null</c> حين لا شيء يُبلَّغ عنه.</summary>
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

    // ---------- المستمع ----------

    /// <summary>ما يرفضه المستمع بنفسه (فوق أربعة اتصالات معلّقة) أُغلق بلا قراءة بايت: غير مصادَق بالتعريف.</summary>
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

    /// <summary>السجل يبقى مقروءًا بعد إغلاق المستمع: التنظيف (القسم 7) يسبق رفع التقرير.</summary>
    [Fact]
    public async Task Listener_ProbesSurviveDisposal()
    {
        var listener = new TunnelListener(0, IPAddress.Loopback);
        listener.Probes.Record(IPAddress.Parse("198.51.100.5"));
        await listener.DisposeAsync();

        Assert.Equal(1, listener.Probes.Count);
        Assert.Equal(new[] { "198.51.100.5" }, listener.Probes.Peers);
    }

    // ---------- الموصّل المتماثل ----------

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
    /// فاحص منافذ يفتح TCP ويكتب هراءً (لا TLS ولا AUTH1) أثناء نافذة الاتصال: يُعدّ محاولة غير مصرَّح بها،
    /// ويظهر في تشخيص الاتصال تحت <c>inbound_unauthenticated</c>.
    /// </summary>
    [Fact]
    public async Task Connector_RecordsAPortScannerThatNeverPassesAuth1()
    {
        await using var host = new HostSide();
        using var peerCert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var unreachablePeer = new PeerEndpointInfo(peerCert.FingerprintHex, new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", Loopback.ClosedPort()) });

        // نافذة اتصال طويلة يقطعها الاختبار بمجرد تسجيل المحاولات، بدل انتظار المهلة كاملة بلا فائدة.
        using var window = new CancellationTokenSource();
        var connect = host.Connector.ConnectAsync(unreachablePeer, TimeSpan.FromSeconds(30), window.Token);

        // ثلاثة "فاحصين": يفتحون المقبس، يكتبون بايتات ليست ClientHello، ثم يغلقون.
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
    /// الاتصال الشرعي لا يُعدّ محاولة. هذا الاختبار كان متقطعًا وكشف عيبًا حقيقيًا لا هشاشة اختبار: الاتصال
    /// المتماثل يفتح <b>اتصالين</b> بين الجهازين، وأحدهما خاسر في كل جلسة ناجحة، والقسم 2 الخطوة 5 يوجب إغلاقه
    /// <b>بلا رد</b> — فكان الطرف الخاسر يسجّله محاولة غير مصرَّح بها، أي إشارة أمنية كاذبة في كل جلسة سليمة.
    /// يُكرَّر السباق عدة مرات لأن ترتيب الفائز غير حتمي.
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
    /// صفوف الاتصالات الواردة في التشخيص محدودة بسقف. بلا سقف تنمو القائمة بعدد ما يفتحه أي طرف على الشبكة،
    /// فتكبر الذاكرة ويتجاوز <c>session.connect_failed</c> حد الـ 64 KB في <c>docs/api.md</c> فيُرفض التشخيص كله.
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
            catch (SocketException) { /* رفض فوق السعة */ }
            catch (IOException) { /* أُغلق */ }
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
        // ثابت دقيق: كل اتصال وارد وصل إلى المعالج يُحسب مرة واحدة — إما صفًا محفوظًا وإما صفًا مُسقطًا.
        Assert.Equal(handled, inboundRows + dropped);
        // العدّاد والعناوين يبقيان محدودين مهما بلغ العدد.
        Assert.True(host.Listener.Probes.Count > 0);
        Assert.True(host.Listener.Probes.Peers.Count <= UnauthenticatedProbeLog.MaxPeers);
    }

    // ---------- ما يقرؤه المسار C ----------

    /// <summary>
    /// المفاتيح الثلاثة على <c>ITunnelSession.Diagnostics</c> بأسمائها في <c>docs/api.md</c> حرفيًا. هذا هو العقد
    /// مع المسار C: يقرؤها كما هي ويضعها في <c>data</c> بلا إعادة تسمية ولا حساب.
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

    /// <summary>جلسة بلا محاولات تنشر المفاتيح بصفر: الصفر هو مقام النسبة عند الخادم، لا غياب المفتاح.</summary>
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
