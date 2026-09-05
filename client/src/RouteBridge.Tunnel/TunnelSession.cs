using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Tunnel;

/// <summary>
/// تنفيذ <see cref="ITunnelSession"/> للدورين معًا: يجمع القطع الموجودة (الشهادة، المستمع، جامع المرشحين، الموصّل المتماثل،
/// الـ Mux، سياسة الخروج على المضيف، الـ Proxy على Guest) في كائن واحد يملك دورة الحياة وترتيب التنظيف.
///
/// دورة الحياة (docs/protocol.md القسمان 2 و7):
///   PrepareAsync → Listening (شهادة + مستمع + مرشحون؛ ما يُرسل في session.endpoint)
///   ConnectAsync → Connecting → Authenticating → Connected (Mux + الطرف الخاص بالدور)
///   موت النفق  → حدث Died بسبب مقترح (لا يُرفع أبدًا لإغلاق نظيف)
///   EndAsync    → Ended (ترتيب التنظيف الست خطوات، آمن من أي حالة ومن التكرار)
///
/// ما يبقى على التطبيق (المسار C): تشغيل المتصفح وإغلاقه (عبر <see cref="TunnelSessionOptions.CloseBrowserAsync"/>)،
/// وكل رسائل الخادم (session.endpoint / connected / connect_failed / stats / end).
///
/// ملكية الأسرار: الجلسة تملك <c>material.Secret</c> وتمسحه في الخطوة 5 من التنظيف.
/// </summary>
public sealed class TunnelSession : ITunnelSession
{
    private readonly SessionMaterial _material;
    private readonly TunnelSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _endGate = new();
    private readonly object _diagnosticsGate = new();
    private readonly Dictionary<string, object?> _diagnostics = new(StringComparer.Ordinal);

    private SessionCertificate? _certificate;
    private TunnelListener? _listener;
    private ICandidateSource? _candidateSource;
    private LocalEndpointInfo? _local;
    private NerdbankMux? _mux;
    private ITunnelEgress? _egress;
    private ITunnelProxy? _proxy;
    private Task? _deathWatch;
    private Task? _endTask;
    private TunnelStats _finalStats = new(0, 0, 0);
    private IReadOnlyCollection<string> _finalDomains = Array.Empty<string>();
    private int _state = (int)TunnelState.Idle;
    private int _connectStarted;
    private int _ending;
    private int _diedRaised;
    private int _probeRaised;

    public TunnelSession(SessionMaterial material, TunnelSessionOptions? options = null)
    {
        _material = material ?? throw new ArgumentNullException(nameof(material));
        if (material.Secret is null || material.Secret.Length != 32)
            throw new ArgumentException("session secret must be exactly 32 bytes", nameof(material));
        _options = options ?? new TunnelSessionOptions();
        Note("role", material.Role == TunnelRole.Host ? "host" : "guest");
        Note("state", TunnelState.Idle.ToString());
        Note("transport", _options.Transport.Name);
    }

    public Guid SessionId => _material.SessionId;
    public TunnelRole Role => _material.Role;
    public TunnelState State => (TunnelState)Volatile.Read(ref _state);

    public event Action<TunnelState>? StateChanged;
    public event Action? ProbeSeen;
    public event Action<TunnelEndReason>? Died;

    public GuestProxyInfo? Proxy { get; private set; }

    /// <summary>وقت أول وصول لصفحة الفحص (للمشتركين المتأخرين على <see cref="ProbeSeen"/>).</summary>
    public DateTimeOffset? ProbeSeenAt { get; private set; }

    /// <summary>تشخيص جمع المرشحين لبوابة قرار Relay (ADR-0003). null قبل PrepareAsync.</summary>
    public GatherDiagnostics? GatherDiagnostics { get; private set; }

    /// <summary>منفذ الاستماع الذي يُرسل في host.available.listen_port. صفر قبل PrepareAsync أو بعد التنظيف.</summary>
    public int ListenPort => _listener?.Port ?? 0;

    /// <summary>بصمة شهادة هذه الجلسة (نفس ما في LocalEndpointInfo). null قبل PrepareAsync.</summary>
    public string? CertificateFingerprintHex => _local?.CertFingerprintSha256Hex;

    /// <summary>هل حُذفت الشهادة (خطوة التنظيف 5)؟ true قبل الإنشاء أيضًا.</summary>
    public bool CertificateDisposed => _certificate?.IsDisposed ?? true;

    /// <summary>هل الـ Mux مغلق (خطوة التنظيف 3)؟ true قبل الاتصال أيضًا.</summary>
    public bool MuxClosed => _mux?.IsClosed ?? true;

    public TunnelStats Stats => State == TunnelState.Ended ? _finalStats : SnapshotStats();

    public IReadOnlyCollection<string> DomainsSeen
        => State == TunnelState.Ended ? _finalDomains : _egress?.Domains ?? Array.Empty<string>();

    public IReadOnlyDictionary<string, object?> Diagnostics
    {
        get { lock (_diagnosticsGate) return new Dictionary<string, object?>(_diagnostics, StringComparer.Ordinal); }
    }

    // ---------- 1. PrepareAsync ----------

    /// <summary>
    /// docs/protocol.md القسم 2 الخطوة 1: شهادة الجلسة + مستمع TCP + تعيين UPnP + جمع المرشحين.
    /// عديم الأثر عند التكرار (يعيد النتيجة نفسها). ينقل الحالة إلى Listening.
    /// </summary>
    public async Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct)
    {
        ThrowIfEnded();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_local is { } prepared) return prepared;
            ThrowIfEnded();

            var certificate = SessionCertificate.Create(_material.ExpiresAt);
            TunnelListener? listener = null;
            ICandidateSource? source = null;
            try
            {
                listener = new TunnelListener(_options.ListenPort, _options.BindAddress);
                source = _options.CandidateSource?.Invoke() ?? new CandidateGatherer(_options.UpnpDiscoveryTimeout);
                var gatherOptions = new GatherOptions(listener.Port, _options.OurPublicIp, _material.SamePublicIp, MappingLifetime(), _options.EnableUpnp);
                var gather = await source.GatherAsync(gatherOptions, ct).ConfigureAwait(false);

                _certificate = certificate;
                _listener = listener;
                _candidateSource = source;
                GatherDiagnostics = gather.Diagnostics;
                _local = new LocalEndpointInfo(certificate.FingerprintHex, gather.Candidates);

                Note("listener_port", listener.Port);
                Note("gather", gather.Diagnostics.ToDictionary());
                Note("local_candidates", DescribeCandidates(gather.Candidates));
                SetState(TunnelState.Listening);
                return _local;
            }
            catch
            {
                certificate.Dispose();
                if (source is not null) await SafeDisposeAsync(source).ConfigureAwait(false);
                if (listener is not null) await SafeDisposeAsync(listener).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 2. ConnectAsync ----------

    /// <summary>
    /// docs/protocol.md القسم 2 الخطوات 3-6 عبر <see cref="SymmetricConnector"/>، ثم الـ Mux والطرف الخاص بالدور:
    /// المضيف يربط سياسة الخروج بـ acceptor، وGuest يشغّل الـ Proxy المحلي ويكشف منفذه ورابط صفحة الفحص.
    /// عند الفشل تبقى الحالة Connecting (المستمع أُغلق) ويعيد النتيجة بسبب الفشل مع تشخيص كل مرشح في <see cref="Diagnostics"/>.
    /// </summary>
    public async Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ThrowIfEnded();
        if (_local is null || _listener is null || _certificate is null)
            throw new InvalidOperationException("PrepareAsync must complete before ConnectAsync");
        ValidateRoleFactories();
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0)
            throw new InvalidOperationException("ConnectAsync may be called only once");

        SetState(TunnelState.Connecting);
        var connector = new SymmetricConnector(_material, _certificate, _listener, _options.Transport, _local.Candidates);
        void OnStage(string stage)
        {
            if (stage == "auth" && State == TunnelState.Connecting) SetState(TunnelState.Authenticating);
        }

        SymmetricConnectOutcome outcome;
        connector.StageReached += OnStage;
        try
        {
            outcome = await connector.ConnectAsync(peer, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            connector.StageReached -= OnStage;
        }

        Note("connect", outcome.Diagnostics);
        if (!outcome.Result.Connected || outcome.Connection is null) return outcome.Result;

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_endTask is not null)
            {
                // انتهت الجلسة بينما كان الاتصال جاريًا: لا نبني نفقًا فوق ما سيُغلق فورًا.
                await SafeDisposeAsync(outcome.Connection).ConfigureAwait(false);
                return new TunnelConnectResult(false, null, outcome.Result.ConnectMs, outcome.Result.TlsVersion, "session_ended");
            }

            // docs/protocol.md القسم 5: النافذة تُشتق من الـ RTT المقيس عند الاتصال، وهذا أول موضع يعرفه
            // (SymmetricConnector عاد للتو). كل طرف يشتق من قياسه هو — نافذة الاستقبال خاصية المستقبل
            // وتُعلَن لكل قناة في بروتوكول Nerdbank 3، فلا حاجة إلى اتفاق على السلك (انظر MuxWindow).
            var muxOptions = _options.Mux ?? new MuxOptions();
            var window = muxOptions.Resolve(MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(outcome.Result.ConnectMs)));
            Note("mux_window", window.ReceiveWindow);
            Note("mux_max_streams", window.MaxConcurrentStreams);
            Note("mux_window_reason", window.Reason); // لماذا هذه الشريحة، وسبب الملاذ عند قياس فاسد
            var mux = NerdbankMux.Create(outcome.Connection.Stream, _material.Role, muxOptions, window);
            try
            {
                if (_material.Role == TunnelRole.Host)
                {
                    var egress = _options.HostEgress!(new TunnelEgressContext(
                        _options.Allowlist, _options.AllowedPorts, _options.Resolver, BlockedLocalAddresses(), mux, window.MaxConcurrentStreams));
                    egress.Attach(mux);
                    _egress = egress;
                }
                else if (_options.StartGuestProxy)
                {
                    var proxy = _options.GuestProxy!(new TunnelProxyContext(
                        _options.Allowlist, _options.AllowedPorts, _options.Resolver, _material.PeerPublicIp, mux));
                    proxy.ProbeSeen += OnProbeSeen;
                    proxy.Start();
                    _proxy = proxy;
                    Proxy = new GuestProxyInfo(proxy.Port, proxy.ProbeUrl);
                    Note("proxy_port", proxy.Port);
                    Note("probe_url", proxy.ProbeUrl);
                }
            }
            catch
            {
                await SafeDisposeAsync(mux).ConfigureAwait(false);
                throw;
            }

            _mux = mux;
            _deathWatch = WatchAsync(mux);
            Note("winner_type", outcome.Result.WinnerType is null ? null : CandidateTypeNames.ToWire(outcome.Result.WinnerType.Value));
            Note("connect_ms", outcome.Result.ConnectMs);
            Note("tls_version", outcome.Result.TlsVersion);
            SetState(TunnelState.Connected);
            return outcome.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 3. الحيوية والموت ----------

    /// <summary>
    /// <c>Completion</c> يفشل = نفق ميت (EOF بلا GOAWAY أو انتهاء مهلة PONG بعد إصلاحَي الأسبوع 2)؛ اكتماله بلا خطأ
    /// إغلاق نظيف لا يُبلَّغ عنه. الحدث يُرفع مرة واحدة ولا يُرفع أثناء EndAsync.
    /// </summary>
    private async Task WatchAsync(NerdbankMux mux)
    {
        try
        {
            await mux.Completion.ConfigureAwait(false);
            Note("mux_completion", "clean");
        }
        catch (Exception e)
        {
            Note("mux_completion", "faulted");
            Note("death", Describe(e));
            RaiseDied(SuggestDeathReason(mux));
        }
    }

    private TunnelEndReason SuggestDeathReason(NerdbankMux mux) => mux.RemoteGoAway switch
    {
        GoAwayReason.Expired => TunnelEndReason.Expired,
        GoAwayReason.ProtocolError => TunnelEndReason.ProtocolError,
        // لا GOAWAY: الطرف الآخر اختفى. الخادم يسمي ذلك بحسب من اختفى (docs/ws-protocol.md القسم 5).
        _ => _material.Role == TunnelRole.Host ? TunnelEndReason.GuestDisconnected : TunnelEndReason.HostDisconnected,
    };

    private void RaiseDied(TunnelEndReason reason)
    {
        if (Volatile.Read(ref _ending) != 0) return;
        if (Interlocked.Exchange(ref _diedRaised, 1) != 0) return;
        Note("died_reason", reason.ToString());
        try { Died?.Invoke(reason); } catch { /* المستمع مسؤول عن أخطائه */ }
    }

    private void OnProbeSeen()
    {
        if (Interlocked.Exchange(ref _probeRaised, 1) != 0) return;
        ProbeSeenAt = DateTimeOffset.UtcNow;
        Note("probe_seen_at", ProbeSeenAt.Value.ToString("O"));
        try { ProbeSeen?.Invoke(); } catch { /* المستمع مسؤول عن أخطائه */ }
    }

    // ---------- 4. EndAsync ----------

    /// <summary>
    /// ترتيب التنظيف في docs/protocol.md القسم 7 حرفيًا. آمن من أي حالة (حتى Idle) ومن التكرار: الاستدعاء الثاني
    /// ينتظر الأول نفسه. لا يرمي أبدًا؛ أخطاء كل خطوة تُسجَّل في <see cref="Diagnostics"/> ولا توقف ما بعدها.
    /// </summary>
    public Task EndAsync(TunnelEndReason reason, CancellationToken ct)
    {
        lock (_endGate)
        {
            if (_endTask is null)
            {
                Volatile.Write(ref _ending, 1); // فورًا: أي انقطاع من الآن متوقَّع ولا يُرفع كموت
                // خارج القفل حتى لا ينفَّذ أي خطاف (المتصفح، StateChanged) وهو محتجَز.
                _endTask = Task.Run(() => EndCoreAsync(reason, ct));
            }
            return _endTask;
        }
    }

    private async Task EndCoreAsync(TunnelEndReason reason, CancellationToken ct)
    {
        Volatile.Write(ref _ending, 1); // من هنا فصاعدًا كل انقطاع متوقَّع: لا حدث Died
        Note("end_reason", reason.ToString());
        // ننتظر أي تجهيز أو ربط جارٍ حتى لا نهدم قطعة قيد الإنشاء (ConnectAsync يفحص _endTask تحت البوابة نفسها).
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await CleanupAsync(reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (_deathWatch is { } watch)
        {
            try { await watch.ConfigureAwait(false); } catch { /* الحارس يبتلع أخطاءه */ }
        }
    }

    private async Task CleanupAsync(TunnelEndReason reason, CancellationToken ct)
    {
        // 1) الـ Proxy يتوقف عن قبول اتصالات جديدة.
        Try("proxy_stop_accepting", () => _proxy?.StopAccepting());

        // 2) إغلاق المتصفح: ملك التطبيق، ينفذه هنا عبر الخطاف ليبقى في موضعه من الترتيب.
        if (_options.CloseBrowserAsync is { } closeBrowser)
            await TryAsync("browser_close", () => closeBrowser(ct)).ConfigureAwait(false);

        // 3) GOAWAY(session_end) وإغلاق النفق، ثم إيقاف الـ Proxy كاملًا (لا وجهة لاتصالاته بعد إغلاق النفق).
        if (_mux is { } mux)
        {
            await TryAsync("mux_goaway", () => mux.CloseAsync(GoAwayFor(reason))).ConfigureAwait(false);
            await TryAsync("mux_dispose", () => mux.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (_proxy is { } proxy)
        {
            proxy.ProbeSeen -= OnProbeSeen;
            await TryAsync("proxy_stop", proxy.StopAsync).ConfigureAwait(false);
            await TryAsync("proxy_dispose", () => proxy.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        // الإحصاءات النهائية تُلتقط قبل التخلص من عدّادات الخروج.
        _finalStats = SnapshotStats() with { OpenStreams = 0 };
        _finalDomains = _egress?.Domains ?? Array.Empty<string>();
        if (_egress is { } egress) await TryAsync("egress_dispose", () => egress.DisposeAsync().AsTask()).ConfigureAwait(false);

        // 4) إغلاق المستمع وإزالة تعيين UPnP.
        if (_listener is { } listener) await TryAsync("listener_dispose", () => listener.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_candidateSource is { } source)
        {
            if (source.HasMapping)
            {
                var removed = false;
                await TryAsync("upnp_remove", async () => removed = await source.RemoveMappingAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
                Note("upnp_mapping_removed", removed);
            }
            await TryAsync("candidate_source_dispose", () => source.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        // 5) Dispose للشهادة ومسح السر.
        Try("certificate_dispose", () => _certificate?.Dispose());
        CryptographicOperations.ZeroMemory(_material.Secret);

        // 6) الإحصاءات والنطاقات جاهزة لـ session.end (يرسلها التطبيق).
        Note("final_bytes_up", _finalStats.BytesUp);
        Note("final_bytes_down", _finalStats.BytesDown);
        Note("final_domains", _finalDomains.Count);
        SetState(TunnelState.Ended);
    }

    public async ValueTask DisposeAsync()
        => await EndAsync(_material.Role == TunnelRole.Host ? TunnelEndReason.HostEnded : TunnelEndReason.GuestEnded, CancellationToken.None).ConfigureAwait(false);

    // ---------- helpers ----------

    private static GoAwayReason GoAwayFor(TunnelEndReason reason) => reason switch
    {
        TunnelEndReason.Expired => GoAwayReason.Expired,
        TunnelEndReason.ProtocolError => GoAwayReason.ProtocolError,
        _ => GoAwayReason.SessionEnd,
    };

    private TunnelStats SnapshotStats()
    {
        var muxStats = _mux?.Stats ?? default;
        // المضيف يبلّغ حمولة المواقع (ما يريده الخادم في session.stats)؛ Guest لا عدّاد له فيبلّغ بايتات النقل.
        if (_material.Role == TunnelRole.Host && _egress is { } egress)
            return new TunnelStats(egress.BytesUp, egress.BytesDown, muxStats.OpenStreams);
        return new TunnelStats(muxStats.BytesUp, muxStats.BytesDown, muxStats.OpenStreams);
    }

    private IReadOnlyList<IPAddress> BlockedLocalAddresses()
    {
        var list = new List<IPAddress>(_options.LocalAddresses ?? LocalNetwork.GetPolicyLocalAddresses());
        if (IPAddress.TryParse(_options.OurPublicIp, out var ours)) list.Add(ours);
        return list;
    }

    private TimeSpan MappingLifetime()
    {
        var remaining = _material.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return remaining + _options.MappingLifetimeSlack;
    }

    private void ValidateRoleFactories()
    {
        if (_material.Role == TunnelRole.Host && _options.HostEgress is null)
            throw new InvalidOperationException("TunnelSessionOptions.HostEgress is required for the host role (use RouteBridge.Egress.EgressTunnelAdapter.Create)");
        if (_material.Role == TunnelRole.Guest && _options.StartGuestProxy && _options.GuestProxy is null)
            throw new InvalidOperationException("TunnelSessionOptions.GuestProxy is required for the guest role (use RouteBridge.Proxy.ProxyTunnelAdapter.Create, or set StartGuestProxy = false)");
    }

    private void ThrowIfEnded()
    {
        if (_endTask is not null || State == TunnelState.Ended)
            throw new InvalidOperationException("tunnel session has ended");
    }

    private void SetState(TunnelState next)
    {
        var previous = (TunnelState)Interlocked.Exchange(ref _state, (int)next);
        if (previous == next) return;
        Note("state", next.ToString());
        try { StateChanged?.Invoke(next); } catch { /* المستمع مسؤول عن أخطائه */ }
    }

    private void Note(string key, object? value)
    {
        lock (_diagnosticsGate) _diagnostics[key] = value;
    }

    private void Try(string step, Action action)
    {
        try { action(); }
        catch (Exception e) { Note("cleanup_error_" + step, Describe(e)); }
    }

    private async Task TryAsync(string step, Func<Task> action)
    {
        try { await action().WaitAsync(_options.CleanupStepTimeout).ConfigureAwait(false); }
        catch (Exception e) { Note("cleanup_error_" + step, Describe(e)); }
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل أثناء التراجع */ }
    }

    private static List<Dictionary<string, object?>> DescribeCandidates(IReadOnlyList<CandidateEndpoint> candidates)
        => candidates.Select(c => new Dictionary<string, object?>
        {
            ["type"] = CandidateTypeNames.ToWire(c.Type),
            ["ip"] = c.Ip,
            ["port"] = c.Port,
        }).ToList();

    // لا أسرار ولا حمولات في النصوص؛ الوصف نوع الخطأ ورسالته فقط.
    private static string Describe(Exception e) => e switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        MuxClosedException m => $"mux_closed: {m.Message}",
        AuthenticationException a => $"tls: {a.Message}",
        SocketException s => $"socket: {s.SocketErrorCode}",
        IOException io when io.InnerException is SocketException s => $"socket: {s.SocketErrorCode}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };
}
