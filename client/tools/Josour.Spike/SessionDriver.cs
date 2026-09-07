using System.Diagnostics;
using System.Net;
using System.Text;
using Josour.Browser;
using Josour.Core.Browser;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Core.Tunnel;
using Josour.Egress;
using Josour.Infrastructure.Session;
using Josour.Proxy;
using Josour.Tunnel;

namespace Josour.Spike;

/// <summary>كيف انتهت الجلسة. يترجمها <see cref="SessionCommand.ExitFor"/> إلى رمز خروج.</summary>
public enum SessionOutcomeKind
{
    /// <summary>لم يُصادَق أي اتصال خلال المهلة (أو رُفض التجهيز). أُرسل <c>session.connect_failed</c>.</summary>
    ConnectFailed,

    /// <summary>النفق مات من تلقائه بعد أن عمل (انقطاع، أو موت عملية الطرف الآخر، أو انتهاء مهلة PONG).</summary>
    TunnelDied,

    /// <summary>الخادم أنهى الجلسة (<c>session.terminate</c>): انتهاء المدة، أو إنهاء الطرف الآخر، أو إنهاء المسؤول.</summary>
    Terminated,

    /// <summary>بلغ <c>expires_at</c> محليًا قبل وصول <c>session.terminate</c>.</summary>
    Expired,

    /// <summary>إنهاء محلي مقصود (Ctrl+C، أو المستخدم ضغط «إنهاء»). أُرسل <c>session.end</c>.</summary>
    LocalEnd,

    /// <summary>قناة التحكم سقطت أثناء الجلسة؛ الخادم ينهيها بـ <c>*_disconnected</c> ولا قيمة لنفق بلا تحكم.</summary>
    ChannelLost,

    /// <summary>الخادم أرسل ما يخالف العقد، أو انتهت الجلسة بسبب محلي آخر (مثل <c>browser_not_proxied</c>).</summary>
    ProtocolError,
}

/// <summary>حصيلة الجلسة كاملة كما تظهر في <c>tool.exit</c>.</summary>
public sealed record SessionOutcome(
    SessionOutcomeKind Kind,
    TunnelEndReason EndReason,
    TunnelStats Stats,
    IReadOnlyCollection<string> Domains,
    string? Detail);

/// <summary>
/// كل ما تحتاجه الأداة حول <see cref="SessionCoordinator"/>: المنسّق نفسه يملك دورة الجلسة كاملة بعد
/// <c>session.created</c> (نفس الشيفرة التي يقودها تطبيق WPF)، وهذا الصنف يوفّر له ما يخص الأداة وحدها —
/// مصنع النفق بلا واجهة، وتغليف القناة والنفق والمتصفح بحيث يخرج كل حدث سطر JSON واحد كما في
/// <c>docs/spike-runbook.md</c>، وحصيلة واحدة تُترجَم إلى رمز الخروج.
///
/// <para>
/// لا منطق جلسة هنا: لا ترتيب تنظيف، ولا قرار سبب إنهاء يُرسل إلى الخادم، ولا مؤقتات. إن احتيج شيء من ذلك
/// فمكانه <see cref="SessionCoordinator"/> لا هنا.
/// </para>
/// </summary>
internal sealed class SessionDriver : ITunnelSessionFactory, IAsyncDisposable
{
    private readonly Args _args;
    private readonly EventLog _log;
    private readonly string? _curlTest;
    private readonly WorkBrowserSession? _browser;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private SessionCoordinator? _coordinator;
    private TunnelSession? _tunnel;
    private Ending? _ending;
    private bool _sessionEndSent;

    public SessionDriver(Args args, EventLog log, string? curlTest, bool wantsBrowser)
    {
        _args = args;
        _log = log;
        _curlTest = curlTest;
        if (wantsBrowser)
        {
            var kind = (args.Get("browser") ?? "chrome").ToLowerInvariant() switch
            {
                "edge" => BrowserKind.Edge,
                _ => BrowserKind.Chrome,
            };
            _browser = new WorkBrowserSession(new SpikeBrowserProvider(kind, args.Get("profile") ?? BrowserCommandLine.DefaultProfileDirectory(), log));
        }
    }

    /// <summary>
    /// متصفح العمل الذي يُسلَّم للمنسّق: الحقيقي مع <c>--browser</c>، وإلا <see cref="NoWorkBrowser"/> الذي يجعل
    /// المنسّق يتخطى التشغيل وانتظار صفحة الفحص بدل أن ينهي الجلسة بـ <c>browser_not_proxied</c> بلا متصفح أصلًا.
    /// </summary>
    public IWorkBrowser Browser => _browser ?? (IWorkBrowser)NoWorkBrowser.Instance;

    /// <summary>الدور، يُعرف من <c>session.created</c> عبر المنسّق (أو من المعاملات قبل ذلك).</summary>
    public TunnelRole Role { get; set; } = TunnelRole.Guest;

    /// <summary>تنتهي عندما يبلغ المنسّق <see cref="SessionPhase.Ended"/> (أي بعد اكتمال ترتيب التنظيف).</summary>
    public Task Ended => _ended.Task;

    // ---------- التوصيل ----------

    /// <summary>يغلّف قناة التحكم ليصدر <c>frame.out</c> لكل إطار يرسله المنسّق (إطارات الأداة نفسها ليست منه).</summary>
    public IControlChannel Wrap(IControlChannel channel) => new EventControlChannel(channel, OnFrameSent, OnFrameFailed);

    public void Attach(SessionCoordinator coordinator)
    {
        _coordinator = coordinator;
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCoordinator.Phase))
            {
                OnPhase(coordinator.Phase);
            }
        };
    }

    /// <summary>إطار وارد يخص مجرى الجلسة: نفس الأحداث التي كان يصدرها سائق الجلسة القديم فوق <c>frame.in</c>.</summary>
    public void OnFrameReceived(ControlMessage message)
    {
        switch (message)
        {
            case SessionCreatedMessage m:
                // من هنا يتولى المنسّق: هذا الحدث يسبق أول خطوة له لأن هذا المشترك يسبقه على القناة.
                _log.Emit("session.created", EventLog.Fields(
                    ("session_id", m.SessionId), ("peer_display_name", m.Peer.UserDisplayName), ("peer_device_name", m.Peer.DeviceName),
                    ("peer_public_ip", m.PeerPublicIp), ("same_public_ip", m.SamePublicIp),
                    ("expires_at", m.ExpiresAt.ToString("O")), ("allowlist_version", m.AllowlistVersion)),
                    $"session {m.SessionId} with {m.Peer.UserDisplayName}/{m.Peer.DeviceName}; expires {m.ExpiresAt:u}");
                break;

            case SessionActiveMessage m:
                _log.Emit("session.active", EventLog.Fields(("expires_at", m.ExpiresAt.ToString("O"))), $"session is active until {m.ExpiresAt:O}");
                break;

            case SessionTerminateMessage m:
                _log.Emit("session.terminate", EventLog.Fields(("reason", m.Reason)), $"the server ended the session: {m.Reason}");
                SetEnding(SessionOutcomeKind.Terminated, SessionEndReasonNames.Parse(m.Reason), m.Reason);
                break;
        }
    }

    /// <summary>سقوط قناة التحكم أثناء الجلسة: الخادم ينهيها بـ <c>*_disconnected</c> ولا قيمة لنفق بلا تحكم.</summary>
    public void OnChannelState(ControlChannelState state)
    {
        if (state == ControlChannelState.Connected || _coordinator?.HasLiveSession != true) return;
        SetEnding(SessionOutcomeKind.ChannelLost, SessionEndReasonNames.LocalDisconnect(Role), state.ToString());
    }

    /// <summary>‏Ctrl+C: إنهاء محلي مقصود بترتيب التنظيف الكامل، لا قتل للعملية.</summary>
    public Task RequestEndAsync()
    {
        SetEnding(SessionOutcomeKind.LocalEnd, SessionEndReasonNames.LocalEnd(Role), "local");
        return _coordinator?.DisconnectAsync() ?? Task.CompletedTask;
    }

    /// <summary>
    /// محاولات الوصول غير المصرَّح بها على مستمع النفق، بالمفاتيح المحجوزة في <c>docs/api.md</c>
    /// (‏<c>POST /api/v1/diagnostics</c>): <c>listener_unauthenticated</c> و<c>listener_port</c>
    /// و<c>unauthenticated_peers</c> (≤ 10 عناوين)، وتُنقل كما هي من <c>ITunnelSession.Diagnostics</c> بلا تعديل
    /// ولا إعادة تسمية. <c>null</c> إن لم يُنشأ نفق أصلًا (فلا مستمع ولا شيء يُبلَّغ عنه).
    ///
    /// <para>تُرفع حتى حين يكون العدّاد صفرًا: الخادم لا يكتب صف <c>security_events</c> إلا على قيمة موجبة، لكن
    /// الجلسات الخالية من المحاولات هي مقام النسبة — وبلا مقام لا يعني «عشر محاولات» شيئًا.</para>
    /// </summary>
    public (Guid SessionId, Dictionary<string, object?> Data)? ListenerDiagnostics()
    {
        var tunnel = Volatile.Read(ref _tunnel);
        if (tunnel is null) return null;
        var diagnostics = tunnel.Diagnostics;
        if (!diagnostics.TryGetValue("listener_unauthenticated", out var count)) return null;

        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["listener_unauthenticated"] = count };
        foreach (var key in new[] { "listener_port", "unauthenticated_peers", "unauthenticated_peers_distinct" })
        {
            if (diagnostics.TryGetValue(key, out var value)) data[key] = value;
        }
        return (tunnel.SessionId, data);
    }

    /// <summary>الحصيلة النهائية بعد <see cref="Ended"/>.</summary>
    public SessionOutcome Outcome()
    {
        var ending = Volatile.Read(ref _ending) ?? new Ending(SessionOutcomeKind.ProtocolError, TunnelEndReason.ProtocolError, "the session ended without a reason");
        var tunnel = Volatile.Read(ref _tunnel);
        return new SessionOutcome(
            ending.Kind,
            ending.Reason,
            tunnel?.Stats ?? new TunnelStats(0, 0, 0),
            tunnel?.DomainsSeen ?? Array.Empty<string>(),
            ending.Detail);
    }

    // ---------- مصنع النفق ----------

    /// <summary>
    /// نفس تركيب <c>Josour.App.Services.TunnelSessionFactory</c> (<see cref="EgressTunnelAdapter.Create"/> على
    /// المضيف و<see cref="ProxyTunnelAdapter.Create"/> على الضيف وخطاف إغلاق المتصفح)، مع ما تزيده الأداة وحدها:
    /// <c>--listen-port</c> و<c>--no-upnp</c>، وفحص PID المالك حين يوجد متصفح حقيقي. بلا متصفح يُمرَّر null إلى
    /// الـ Proxy: لا عملية متصفح تملك الاتصالات المحلية، والفحص كان سيرفض طلب <c>--curl-test</c> نفسه.
    /// </summary>
    public ITunnelSession Create(TunnelSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Role = request.Material.Role;
        IBrowserSession? proxyBrowser = request.Browser is NoWorkBrowser ? null : request.Browser;

        // خطوة التنظيف 2: خطاف المنسّق نفسه، مع سطر بشري يوضّح ما يجري حين يوجد متصفح فعلًا.
        Func<CancellationToken, Task> closeBrowser = request.CloseBrowserAsync;
        if (proxyBrowser is not null)
        {
            closeBrowser = async token =>
            {
                _log.Note("closing the work browser (WM_CLOSE, then the job object after 3 s)");
                await request.CloseBrowserAsync(token).ConfigureAwait(false);
            };
        }

        var options = new TunnelSessionOptions
        {
            Allowlist = request.Allowlist,
            AllowedPorts = request.AllowedPorts,
            OurPublicIp = request.OurPublicIp,
            ListenPort = _args.GetInt("listen-port", 0),
            EnableUpnp = !_args.Has("no-upnp"),
            HostEgress = EgressTunnelAdapter.Create,
            GuestProxy = context => ProxyTunnelAdapter.Create(context, proxyBrowser, proxyBrowser is null ? null : OwnerChecker()),
            CloseBrowserAsync = closeBrowser,
        };

        var tunnel = new TunnelSession(request.Material, options);
        Volatile.Write(ref _tunnel, tunnel);
        return new EventTunnelSession(tunnel, this);
    }

    private static IOwnerPidChecker OwnerChecker()
        => OperatingSystem.IsWindows() ? WindowsOwnerPidChecker.Instance : PermissiveOwnerPidChecker.Instance;

    // ---------- ما بعد الاتصال على جانب الضيف ----------

    /// <summary>
    /// الإثبات المباشر بلا متصفح: يُنفَّذ مرة واحدة بعد نجاح الاتصال على الضيف. أخطاؤه تُسجَّل ولا تُنهي الجلسة.
    /// (تشغيل متصفح العمل ليس هنا: المنسّق يشغّله عند <c>session.active</c> كما يفعل التطبيق.)
    /// </summary>
    private async Task AfterConnectedAsync(GuestProxyInfo proxy, CancellationToken ct)
    {
        _log.Emit("proxy.ready", EventLog.Fields(("port", proxy.Port), ("probe_url", proxy.ProbeUrl)),
            $"local proxy on 127.0.0.1:{proxy.Port} — point a browser at it, or open {proxy.ProbeUrl}");

        if (_curlTest is not null)
        {
            await CurlThroughProxyAsync(proxy.Port, _curlTest, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// طلب HTTP GET عبر الـ Proxy المحلي نفسه. موقع يعكس عنوان الطالب (مثل <c>https://api.ipify.org</c>) يجعل الجسم
    /// نفسه هو الدليل على أن الخروج تم من عنوان المضيف.
    /// </summary>
    private async Task CurlThroughProxyAsync(int proxyPort, string url, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", BypassOnLocal: false),
                UseProxy = true,
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Josour.Spike/session");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var prefix = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).ReplaceLineEndings(" ").Trim();
            _log.Emit("curl.result", EventLog.Fields(
                ("url", url), ("status", (int)response.StatusCode), ("elapsed_ms", clock.ElapsedMilliseconds),
                ("bytes", bytes.Length), ("body_prefix", prefix)),
                $"GET {url} through the proxy → {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms, {bytes.Length} B: {prefix}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Emit("curl.failed", EventLog.Fields(("url", url), ("elapsed_ms", clock.ElapsedMilliseconds), ("error", $"{e.GetType().Name}: {e.Message}")),
                $"GET {url} through the proxy failed after {clock.ElapsedMilliseconds} ms: {e.Message}");
        }
    }

    // ---------- الأحداث ----------

    private void OnPhase(SessionPhase phase)
    {
        switch (phase)
        {
            case SessionPhase.Ending:
            {
                var ending = Fallback();
                _log.Emit("session.ending", EventLog.Fields(
                    ("kind", ending.Kind.ToString()), ("reason", ending.Reason.ToString()), ("detail", ending.Detail)),
                    $"cleaning up ({ending.Reason})");
                break;
            }

            case SessionPhase.Ended:
            {
                var outcome = Outcome();
                _log.Emit("session.ended", EventLog.Fields(
                    ("kind", outcome.Kind.ToString()), ("reason", outcome.EndReason.ToString()), ("detail", outcome.Detail),
                    ("bytes_up", outcome.Stats.BytesUp), ("bytes_down", outcome.Stats.BytesDown),
                    ("domains", outcome.Domains.ToList()), ("server_already_ended", !Volatile.Read(ref _sessionEndSent))),
                    $"session ended: {outcome.Kind}");
                _ended.TrySetResult();
                break;
            }
        }
    }

    private void OnFrameSent(ControlMessage message)
    {
        _log.Emit("frame.out", EventLog.Fields(("type", message.Type)), $"→ {message.Type}");
        switch (message)
        {
            case SessionStatsMessage m:
                var open = Volatile.Read(ref _tunnel)?.Stats.OpenStreams ?? 0;
                _log.Emit("session.stats", EventLog.Fields(("bytes_up", m.BytesUp), ("bytes_down", m.BytesDown), ("open_streams", open)),
                    $"stats: {m.BytesUp} B up / {m.BytesDown} B down, {open} open stream(s)");
                break;

            case SessionConnectFailedMessage m:
                var reason = m.Diagnostics.TryGetValue("failure_reason", out var value) ? value : null;
                _log.Emit("tunnel.connect_failed", ConnectFailedFields(m), $"connect FAILED: {Text(reason)}");
                SetEnding(SessionOutcomeKind.ConnectFailed, TunnelEndReason.ConnectFailed, reason?.ToString());
                break;

            case SessionEndMessage:
                Volatile.Write(ref _sessionEndSent, true);
                break;
        }
    }

    private void OnFrameFailed(ControlMessage message, Exception error)
        => _log.Emit("frame.out_failed", EventLog.Fields(("type", message.Type), ("error", $"{error.GetType().Name}: {error.Message}")));

    /// <summary>تشخيص الاتصال كما كان: محتوى <c>connect</c> مسطّحًا، مع <c>reason</c> و<c>role</c>.</summary>
    private Dictionary<string, object?> ConnectFailedFields(SessionConnectFailedMessage message)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (message.Diagnostics.TryGetValue("connect", out var connect) && connect is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, value) in map) fields[key] = value;
        }

        fields["reason"] = message.Diagnostics.TryGetValue("failure_reason", out var reason) ? reason : "unknown";
        fields["role"] = Role == TunnelRole.Host ? "host" : "guest";
        return fields;
    }

    /// <summary>أول شرط نهاية هو الحقيقي؛ اللاحق يُهمَل (كما كان في السائق القديم).</summary>
    private void SetEnding(SessionOutcomeKind kind, TunnelEndReason reason, string? detail)
    {
        lock (_gate)
        {
            _ending ??= new Ending(kind, reason, detail);
        }
    }

    /// <summary>لا مصدر صريح (انتهاء مدة، <c>browser_not_proxied</c>، خطأ بروتوكول): نقرأ حكم المنسّق نفسه.</summary>
    private Ending Fallback()
    {
        var wire = _coordinator?.LastEndReason;
        SessionEndReasonNames.TryParse(wire, out var reason);
        var kind = reason switch
        {
            TunnelEndReason.Expired => SessionOutcomeKind.Expired,
            TunnelEndReason.GuestEnded or TunnelEndReason.HostEnded => SessionOutcomeKind.LocalEnd,
            TunnelEndReason.ConnectFailed => SessionOutcomeKind.ConnectFailed,
            TunnelEndReason.GuestDisconnected or TunnelEndReason.HostDisconnected => SessionOutcomeKind.TunnelDied,
            _ => SessionOutcomeKind.ProtocolError,
        };

        lock (_gate)
        {
            return _ending ??= new Ending(kind, reason, wire);
        }
    }

    private static string Text(object? value) => value?.ToString() ?? "null";

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record Ending(SessionOutcomeKind Kind, TunnelEndReason Reason, string? Detail);

    // ---------- التغليف ----------

    /// <summary>
    /// قناة التحكم كما يراها المنسّق: كل شيء يمر كما هو، وكل إطار صادر يصير <c>frame.out</c>. لا تملك القناة
    /// الداخلية ولا تغلقها (‏<see cref="SessionStack"/> يملكها).
    /// </summary>
    private sealed class EventControlChannel : IControlChannel
    {
        private readonly IControlChannel _inner;
        private readonly Action<ControlMessage> _sent;
        private readonly Action<ControlMessage, Exception> _failed;

        public EventControlChannel(IControlChannel inner, Action<ControlMessage> sent, Action<ControlMessage, Exception> failed)
        {
            _inner = inner;
            _sent = sent;
            _failed = failed;
            _inner.MessageReceived += Forward;
            _inner.StateChanged += ForwardState;
            _inner.Closed += ForwardClosed;
        }

        public ControlChannelState State => _inner.State;

        public event Action<ControlChannelState>? StateChanged;

        public event Action<ControlMessage>? MessageReceived;

        public event Action<ControlChannelClosed>? Closed;

        public Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct)
            => _inner.ConnectAsync(accessToken, deviceId, diagnostics, ct);

        public async Task SendAsync(ControlMessage message, CancellationToken ct)
        {
            try
            {
                await _inner.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failed(message, e);
                throw;
            }

            _sent(message);
        }

        public async Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct)
        {
            ControlMessage reply;
            try
            {
                reply = await _inner.RequestAsync(message, timeout, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failed(message, e);
                throw;
            }

            _sent(message);
            return reply;
        }

        public ValueTask DisposeAsync()
        {
            _inner.MessageReceived -= Forward;
            _inner.StateChanged -= ForwardState;
            _inner.Closed -= ForwardClosed;
            return ValueTask.CompletedTask;
        }

        private void Forward(ControlMessage message) => MessageReceived?.Invoke(message);

        private void ForwardState(ControlChannelState state) => StateChanged?.Invoke(state);

        private void ForwardClosed(ControlChannelClosed closed) => Closed?.Invoke(closed);
    }

    /// <summary>
    /// النفق كما يراه المنسّق: كل شيء يمر إلى <see cref="TunnelSession"/> الحقيقي، وكل خطوة تصير حدثًا في مجرى JSON.
    /// وهذا أيضًا مكان ما بعد الاتصال على الضيف (منفذ الـ Proxy و<c>--curl-test</c>)، تمامًا حيث كان سابقًا.
    /// </summary>
    private sealed class EventTunnelSession : ITunnelSession
    {
        private readonly TunnelSession _inner;
        private readonly SessionDriver _driver;

        public EventTunnelSession(TunnelSession inner, SessionDriver driver)
        {
            _inner = inner;
            _driver = driver;
            _inner.StateChanged += OnState;
            _inner.ProbeSeen += OnProbe;
            _inner.Died += OnDied;
        }

        public Guid SessionId => _inner.SessionId;

        public TunnelRole Role => _inner.Role;

        public TunnelState State => _inner.State;

        public TunnelStats Stats => _inner.Stats;

        public IReadOnlyCollection<string> DomainsSeen => _inner.DomainsSeen;

        public GuestProxyInfo? Proxy => _inner.Proxy;

        public IReadOnlyDictionary<string, object?> Diagnostics => _inner.Diagnostics;

        public event Action<TunnelState>? StateChanged;

        public event Action? ProbeSeen;

        public event Action<TunnelEndReason>? Died;

        public async Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct)
        {
            LocalEndpointInfo local;
            try
            {
                local = await _inner.PrepareAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _driver._log.Emit("tunnel.prepare_failed", EventLog.Fields(("error", $"{e.GetType().Name}: {e.Message}")));
                throw;
            }

            _driver._log.Emit("tunnel.prepared", EventLog.Fields(
                ("listen_port", _inner.ListenPort),
                ("cert_fp_sha256", local.CertFingerprintSha256Hex),
                ("candidates", Describe(local.Candidates))),
                $"listening on port {_inner.ListenPort}; sending session.endpoint");
            return local;
        }

        public async Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
        {
            _driver._log.Emit("tunnel.peer_endpoint", EventLog.Fields(
                ("cert_fp_sha256", peer.CertFingerprintSha256Hex), ("candidates", Describe(peer.Candidates))),
                "peer endpoint received; dialling every candidate in parallel");

            var result = await _inner.ConnectAsync(peer, timeout, ct).ConfigureAwait(false);
            if (!result.Connected)
            {
                return result; // the coordinator answers with session.connect_failed, which raises tunnel.connect_failed
            }

            // النافذة مشتقة من connect_ms (docs/protocol.md القسم 5): تُطبع في التشغيل الميداني لأن الشريحة
            // تفسّر سقف التنزيل الواحد وحد الـ streams المتزامنة في نفس التشغيل.
            _driver._log.Emit("tunnel.connected", EventLog.Fields(
                ("winner_type", result.WinnerType is null ? null : CandidateTypeNames.ToWire(result.WinnerType.Value)),
                ("connect_ms", result.ConnectMs), ("tls_version", result.TlsVersion),
                ("mux_window", _inner.Diagnostics.TryGetValue("mux_window", out var window) ? window : null),
                ("mux_max_streams", _inner.Diagnostics.TryGetValue("mux_max_streams", out var streams) ? streams : null),
                ("proxy_port", _inner.Proxy?.Port), ("probe_url", _inner.Proxy?.ProbeUrl)),
                $"CONNECTED via {Text(result.WinnerType is null ? null : CandidateTypeNames.ToWire(result.WinnerType.Value))} in {result.ConnectMs} ms over TLS {Text(result.TlsVersion)}");

            if (_inner.Proxy is { } proxy)
            {
                try
                {
                    await _driver.AfterConnectedAsync(proxy, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _driver._log.Emit("after_connected_failed", EventLog.Fields(("error", $"{e.GetType().Name}: {e.Message}")));
                }
            }

            return result;
        }

        public Task EndAsync(TunnelEndReason reason, CancellationToken ct) => _inner.EndAsync(reason, ct);

        public ValueTask DisposeAsync()
        {
            _inner.StateChanged -= OnState;
            _inner.ProbeSeen -= OnProbe;
            _inner.Died -= OnDied;
            return _inner.DisposeAsync();
        }

        private static List<Dictionary<string, object?>> Describe(IReadOnlyList<CandidateEndpoint> candidates)
            => candidates.Select(c => new Dictionary<string, object?>
            {
                ["type"] = CandidateTypeNames.ToWire(c.Type),
                ["ip"] = c.Ip,
                ["port"] = c.Port,
            }).ToList();

        private void OnState(TunnelState state)
        {
            _driver._log.Emit("tunnel.state", EventLog.Fields(("state", state.ToString())), $"tunnel → {state}");
            StateChanged?.Invoke(state);
        }

        private void OnProbe() => ProbeSeen?.Invoke();

        private void OnDied(TunnelEndReason reason)
        {
            _driver._log.Emit("tunnel.died", EventLog.Fields(("suggested_reason", reason.ToString())), $"the tunnel died: {reason}");
            _driver.SetEnding(SessionOutcomeKind.TunnelDied, reason, reason.ToString());
            Died?.Invoke(reason);
        }
    }

    /// <summary>متصفح العمل الحقيقي للأداة: نوع واحد (‏<c>--browser</c>) وملف تعريف واحد (‏<c>--profile</c>).</summary>
    private sealed class SpikeBrowserProvider : IWorkBrowserProvider
    {
        private readonly BrowserKind _kind;
        private readonly EventLog _log;

        public SpikeBrowserProvider(BrowserKind kind, string profileDirectory, EventLog log)
        {
            _kind = kind;
            _log = log;
            ProfileDirectory = profileDirectory;
        }

        public string ProfileDirectory { get; }

        public IReadOnlyList<BrowserKind> Preference() => new[] { _kind };

        public IBrowserSession Create(BrowserKind kind) => new EventBrowserSession(new BrowserLauncher(), kind, _log);
    }

    /// <summary>‏<see cref="BrowserLauncher"/> يصدر حدث <c>browser.launch</c> بنتيجة كل تشغيل.</summary>
    private sealed class EventBrowserSession : IBrowserSession
    {
        private readonly BrowserLauncher _inner;
        private readonly BrowserKind _kind;
        private readonly EventLog _log;

        public EventBrowserSession(BrowserLauncher inner, BrowserKind kind, EventLog log)
        {
            _inner = inner;
            _kind = kind;
            _log = log;
        }

        public bool IsRunning => _inner.IsRunning;

        public bool OwnsProcess(int pid) => _inner.OwnsProcess(pid);

        public async Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
        {
            var result = await _inner.LaunchAsync(options, ct).ConfigureAwait(false);
            _log.Emit("browser.launch", EventLog.Fields(
                ("browser", _kind.ToString().ToLowerInvariant()), ("success", result.Success), ("failure", result.Failure?.ToString()),
                ("detail", result.Detail), ("handoff", _inner.InstanceHandoff), ("pid", _inner.ProcessId)),
                result.Success ? $"launched {_kind} on the check page" : $"could not launch {_kind}: {result.Failure} — {result.Detail}");
            return result;
        }

        public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => _inner.CloseAsync(graceful, ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
