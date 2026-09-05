using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Control;
using RouteBridge.Core.Tunnel;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.Spike;

/// <summary>
/// المكافئ بلا واجهة لتطبيق WPF: يقود المكدّس الحقيقي كاملًا (ApiClient ← AuthSession ← ControlChannel على WSS ←
/// SessionCoordinator ← TunnelSession ← Egress/Proxy) حتى يستطيع QA تشغيل جلسة حقيقية بين جهازي Windows.
///
/// <para>
/// الأمر <b>سائق رفيع</b> عمدًا: كل ما بعد <c>session.created</c> يملكه <see cref="SessionCoordinator"/> في
/// <c>RouteBridge.Infrastructure</c> — وهو نفسه ما يقوده تطبيق WPF — وكل ترتيب التنظيف يملكه <c>TunnelSession</c>.
/// ما يبقى هنا هو ما يخص الأداة وحدها: تحليل المعاملات، الدخول، <c>host.available</c>، دورة الطلب (قبول تلقائي أو
/// إنشاء)، وطباعة الأحداث؛ وما يخص الأداة من تركيب النفق وأحداثه في <see cref="SessionDriver"/>.
/// إن كبر هذا الملف بمنطق جلسة، فمكانه <c>SessionCoordinator</c> لا هنا.
/// </para>
///
/// <para>المخرجات: سطر JSON لكل حدث على stdout (JSONL)، وملخص بشري على stderr. الأكواد في <see cref="ExitCodes"/>.</para>
/// </summary>
public static class SessionCommand
{
    public static class ExitCodes
    {
        /// <summary>الجلسة عملت وانتهت نهاية متوقَّعة (إنهاء أحد الطرفين، انتهاء المدة، Ctrl+C)، أو <c>--list-hosts</c> نجح.</summary>
        public const int Success = 0;

        /// <summary>سوء استعمال: معاملات ناقصة أو متناقضة، أو مضيف مطلوب غير موجود.</summary>
        public const int Usage = 1;

        /// <summary>لم يقم نفق: فشل الاتصال المتماثل، أو رُفض الطلب، أو انتهت مهلته، أو المضيف غير متاح.</summary>
        public const int ConnectFailed = 2;

        /// <summary>النفق قام ثم مات (انقطاع الطرف الآخر، أو سقوط قناة التحكم أثناء الجلسة).</summary>
        public const int TunnelDied = 3;

        /// <summary>فشل المصادقة أو خرق للعقد من الخادم.</summary>
        public const int ProtocolOrAuth = 4;

        /// <summary>Ctrl+C قبل قيام الجلسة.</summary>
        public const int Interrupted = 130;
    }

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HostsSnapshotTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var roleText = args.Require("role").ToLowerInvariant();
        if (roleText is not ("host" or "guest")) throw new UsageException("--role must be host or guest");
        var isHost = roleText == "host";

        var api = args.Require("api");
        var email = args.Require("email");
        var password = args.Require("password");
        var minutes = args.GetInt("minutes", 30);
        var stateDirectory = args.Get("state-dir") ?? Path.Combine(AppContext.BaseDirectory, "spike-state");
        var listHosts = args.Has("list-hosts");
        var hostDevice = args.Get("host-device");
        var curlTest = args.Get("curl-test");
        var wantsBrowser = args.Has("browser");

        if (!isHost && !listHosts && string.IsNullOrWhiteSpace(hostDevice))
            throw new UsageException("--host-device <id|name> is required for --role guest (or use --list-hosts)");
        if (isHost && (listHosts || hostDevice is not null || curlTest is not null))
            throw new UsageException("--list-hosts, --host-device and --curl-test are guest-only options");
        if (wantsBrowser && curlTest is not null)
            throw new UsageException("--browser and --curl-test cannot be combined: with a browser the proxy enforces the owner-PID check and would reject this tool's own request");
        if (wantsBrowser && isHost)
            throw new UsageException("--browser is a guest-only option");
        if (minutes is < 1 or > 1440) throw new UsageException("--minutes must be between 1 and 1440");

        var log = new EventLog(roleText) { Human = !args.Has("quiet") };
        log.Emit("tool.start", EventLog.Fields(
            ("api", api),
            ("state_dir", Path.GetFullPath(stateDirectory)),
            ("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            ("reset_device", args.Has("reset-device"))),
            $"starting; state in {Path.GetFullPath(stateDirectory)}");

        await using var stack = await SessionStack.CreateAsync(api, stateDirectory, args.Has("reset-device"), null, ct);

        // ---------- 1. الدخول ----------
        try
        {
            await stack.Auth.SignInAsync(email, password, ct);
        }
        catch (ApiException e)
        {
            log.Emit("auth.failed", EventLog.Fields(("code", e.Code), ("status", (int)e.StatusCode), ("message", e.Message)), $"sign-in failed: {e.Code} — {e.Message}");
            return ExitCodes.ProtocolOrAuth;
        }
        catch (ApiUnavailableException e)
        {
            log.Emit("auth.failed", EventLog.Fields(("code", "unavailable"), ("message", e.Message)), $"sign-in could not reach the server: {e.Message}");
            return ExitCodes.ProtocolOrAuth;
        }

        var user = stack.Auth.CurrentUser!;
        var deviceId = stack.Auth.DeviceId!.Value;
        log.Emit("auth.signed_in", EventLog.Fields(
            ("user_id", user.Id), ("email", user.Email), ("display_name", user.DisplayName),
            ("device_id", deviceId), ("device_name", stack.Device.DeviceName), ("app_version", stack.Device.AppVersion)),
            $"signed in as {user.DisplayName} <{user.Email}> on device {stack.Device.DeviceName} ({deviceId})");

        // ---------- 2. قناة التحكم ----------
        await using var driver = new SessionDriver(args, log, curlTest, wantsBrowser) { Role = isHost ? TunnelRole.Host : TunnelRole.Guest };

        stack.Channel.StateChanged += state =>
        {
            log.Emit("channel.state", EventLog.Fields(("state", state.ToString())), $"control channel {state}");
            driver.OnChannelState(state);
        };
        stack.Channel.Closed += closed => log.Emit("channel.closed", EventLog.Fields(
            ("reason", closed.Reason.ToString()), ("close_code", closed.CloseCode), ("description", closed.Description)),
            $"control channel closed for good: {closed.Reason} ({closed.CloseCode})");

        using var frames = new FrameWatcher(stack.Channel);
        frames.Frame += message =>
        {
            log.Emit("frame.in", Describe(message), Summarize(message));
            driver.OnFrameReceived(message);
        };

        // المنسّق يشترك في القناة من الآن — قبل ConnectAsync — حتى يرى hello.ack (المنافذ المسموحة والـ IP العام)
        // وكل إطار بعده بلا فجوة، تمامًا كما يفعل التطبيق عند الإقلاع.
        await using var channel = driver.Wrap(stack.Channel);
        using var coordinator = new SessionCoordinator(channel, stack.Api, driver, driver.Browser, time: TimeProvider.System, options: SessionCoordinatorOptions.Default with
        {
            ConnectTimeout = TimeSpan.FromSeconds(args.GetInt("connect-timeout-s", 30)),
            StatsInterval = TimeSpan.FromSeconds(args.GetInt("stats-interval-s", 30)),
        });
        driver.Attach(coordinator);

        var hostsSnapshot = frames.WaitAsync(m => m is HostsMessage { Type: "hosts.snapshot" });

        HelloAckMessage ack;
        try
        {
            var token = await stack.Auth.GetValidAccessTokenAsync(ct) ?? throw new InvalidOperationException("no access token after sign-in");
            var diagnostics = await RouteBridge.Tunnel.Diagnostics.HostDiagnostics.CollectAsync(ct);
            ack = await stack.Channel.ConnectAsync(token, deviceId, diagnostics, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Emit("channel.connect_failed", EventLog.Fields(("error", $"{e.GetType().Name}: {e.Message}")), $"could not open the control channel: {e.Message}");
            return ExitCodes.ProtocolOrAuth;
        }

        log.Emit("hello.ack", EventLog.Fields(
            ("server_time", ack.ServerTime.ToString("O")), ("public_ip", ack.PublicIp),
            ("allowlist_version", ack.AllowlistVersion), ("max_session_minutes", ack.Settings.MaxSessionMinutes),
            ("request_timeout_seconds", ack.Settings.RequestTimeoutSeconds), ("allowed_ports", ack.Settings.AllowedPorts.ToList()),
            ("log_domains", ack.Settings.LogDomains)),
            $"connected; our public ip is {ack.PublicIp}, allowed ports {string.Join(",", ack.Settings.AllowedPorts)}, allowlist v{ack.AllowlistVersion}");

        // ---------- 3. قائمة المواقع ----------
        // للحدث وحده: المنسّق يحمّل القائمة بنفسه عند session.created بإصدار الجلسة (نفس المحلل ونفس المصدر).
        try
        {
            var (parsed, version, skipped) = await stack.LoadAllowlistAsync(ct);
            log.Emit("allowlist.loaded", EventLog.Fields(("version", version), ("entries", parsed.Entries.Count), ("skipped", skipped)),
                $"allowlist v{version}: {parsed.Entries.Count} entries" + (skipped > 0 ? $" ({skipped} unparsable entries skipped)" : string.Empty));
        }
        catch (Exception e) when (e is ApiException or ApiUnavailableException)
        {
            log.Emit("allowlist.failed", EventLog.Fields(("error", e.Message)), $"could not load the allowlist: {e.Message}");
            return ExitCodes.ProtocolOrAuth;
        }

        // ---------- 4. الدور ----------
        // (‏حدث session.created نفسه يصدره SessionDriver من الإطار الوارد، قبل أن يبدأ المنسّق التجهيز.)
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var pending = isHost
                ? await AwaitIncomingRequestAsync(stack, frames, log, args, interrupt.Token)
                : await CreateRequestAsync(stack, frames, log, hostsSnapshot, hostDevice, minutes, listHosts, interrupt.Token);
            if (pending.ExitCode is int early) return early;
        }
        catch (OperationCanceledException)
        {
            log.Emit("tool.interrupted", null, "interrupted before a session was created");
            return ExitCodes.Interrupted;
        }

        // ---------- 5. الجلسة ----------
        // من هنا فصاعدًا لا شيء يجري في هذا الملف: المنسّق رأى session.created على القناة نفسها ويقود الباقي.

        // Ctrl+C أثناء الجلسة = إنهاء مقصود بترتيب التنظيف الكامل، لا قتل للعملية.
        void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            log.Note("Ctrl+C: ending the session with the protocol's cleanup order");
            _ = driver.RequestEndAsync();
        }

        Console.CancelKeyPress += OnCancelKey;
        SessionOutcome outcome;
        try
        {
            await driver.Ended;
            outcome = driver.Outcome();
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKey;
        }

        var exit = ExitFor(outcome.Kind);
        log.Emit("tool.exit", EventLog.Fields(
            ("code", exit), ("kind", outcome.Kind.ToString()), ("end_reason", outcome.EndReason.ToString()),
            ("bytes_up", outcome.Stats.BytesUp), ("bytes_down", outcome.Stats.BytesDown), ("domains", outcome.Domains.ToList()),
            ("detail", outcome.Detail)),
            $"exit {exit}: {outcome.Kind} ({outcome.Detail ?? outcome.EndReason.ToString()}); {outcome.Stats.BytesUp} B up / {outcome.Stats.BytesDown} B down");
        return exit;
    }

    // ---------- المضيف ----------

    private readonly record struct Pending(SessionCreatedMessage? Created, int? ExitCode);

    private static async Task<Pending> AwaitIncomingRequestAsync(SessionStack stack, FrameWatcher frames, EventLog log, Args args, CancellationToken ct)
    {
        var autoReject = args.Has("auto-reject");

        // ‏host.available هو الافتراضي (بدونه لا يظهر هذا الجهاز في قائمة أي مستخدم فالانتظار عبثي)؛
        // ‏--available شكل صريح للافتراضي، و--no-available يبقي المضيف متصلًا وغير معلن (اختبار القناة وحدها).
        var announce = !args.Has("no-available");
        // ‏listen_port اختياري: مع قيمة يشغّل الخادم فحص قابلية الوصول (docs/ws-protocol.md القسم 6). نمرر
        // نفس المنفذ الذي سيربطه مستمع النفق (--listen-port) حتى يكون الفحص ذا معنى.
        var listenPort = args.GetInt("listen-port", 0);
        if (announce)
        {
            await stack.Channel.SendAsync(new HostAvailableMessage(true, listenPort > 0 ? listenPort : null), ct);
            log.Emit("host.available", EventLog.Fields(("available", true), ("listen_port", listenPort > 0 ? listenPort : null)),
                $"announced host.available=true{(listenPort > 0 ? $" with listen_port {listenPort}" : string.Empty)}; waiting for an incoming request (Ctrl+C to stop)");
        }
        else
        {
            log.Emit("host.available", EventLog.Fields(("available", false)),
                "NOT announcing host.available (--no-available): no guest can see this device; waiting anyway");
        }

        var incoming = (RequestIncomingMessage)await frames.WaitAsync(m => m is RequestIncomingMessage).WaitAsync(ct);
        log.Emit("request.incoming", EventLog.Fields(
            ("request_id", incoming.RequestId), ("guest_name", incoming.GuestName), ("guest_device", incoming.GuestDevice),
            ("duration_min", incoming.DurationMin), ("expires_at", incoming.ExpiresAt.ToString("O"))),
            $"request {incoming.RequestId} from {incoming.GuestName}/{incoming.GuestDevice} for {incoming.DurationMin} min");

        if (autoReject)
        {
            await stack.Channel.SendAsync(new RequestRejectMessage(ControlRef.Next(), incoming.RequestId), ct);
            log.Emit("request.rejected", EventLog.Fields(("request_id", incoming.RequestId)), "auto-rejected (--auto-reject)");
            return new Pending(null, ExitCodes.Success);
        }

        var createdTask = frames.WaitAsync(m => m is SessionCreatedMessage);
        await stack.Channel.SendAsync(new RequestAcceptMessage(ControlRef.Next(), incoming.RequestId), ct);
        log.Emit("request.accepted", EventLog.Fields(("request_id", incoming.RequestId)), "auto-accepted");

        try
        {
            return new Pending((SessionCreatedMessage)await createdTask.WaitAsync(ReplyTimeout, ct), null);
        }
        catch (TimeoutException)
        {
            log.Emit("request.no_session", EventLog.Fields(("request_id", incoming.RequestId)), "accepted but no session.created arrived in time");
            return new Pending(null, ExitCodes.ConnectFailed);
        }
    }

    // ---------- المستخدم ----------

    private static async Task<Pending> CreateRequestAsync(
        SessionStack stack,
        FrameWatcher frames,
        EventLog log,
        Task<ControlMessage> hostsSnapshot,
        string? hostDevice,
        int minutes,
        bool listOnly,
        CancellationToken ct)
    {
        IReadOnlyList<HostInfoDto> hosts;
        try
        {
            hosts = ((HostsMessage)await hostsSnapshot.WaitAsync(HostsSnapshotTimeout, ct)).Hosts;
        }
        catch (TimeoutException)
        {
            // القناة لم ترسل اللقطة بعد hello.ack: نسقط إلى REST بالشكل نفسه والتصفية نفسها (docs/api.md).
            log.Note("no hosts.snapshot within 10 s; falling back to GET /hosts");
            hosts = await stack.Api.GetHostsAsync(ct);
        }

        log.Emit("hosts", EventLog.Fields(("count", hosts.Count), ("hosts", hosts.Select(h => new Dictionary<string, object?>
        {
            ["device_id"] = h.DeviceId,
            ["user_display_name"] = h.UserDisplayName,
            ["device_name"] = h.DeviceName,
            ["reachable"] = h.Reachable,
        }).ToList())), $"{hosts.Count} host(s) available");

        if (listOnly)
        {
            foreach (var host in hosts)
            {
                log.Note($"  {host.DeviceId}  {host.UserDisplayName} / {host.DeviceName}  reachable={host.Reachable?.ToString() ?? "unknown"}");
            }

            return new Pending(null, ExitCodes.Success);
        }

        var target = ResolveHost(hosts, hostDevice!);
        if (target is null)
        {
            log.Emit("host.not_found", EventLog.Fields(("wanted", hostDevice), ("available", hosts.Count)),
                $"no available host matches '{hostDevice}' (use --list-hosts to see the {hosts.Count} on offer)");
            return new Pending(null, ExitCodes.Usage);
        }

        log.Emit("host.selected", EventLog.Fields(("device_id", target.DeviceId), ("device_name", target.DeviceName), ("user_display_name", target.UserDisplayName), ("reachable", target.Reachable)),
            $"asking {target.UserDisplayName}/{target.DeviceName} for {minutes} min");

        var resultTask = frames.WaitAsync(m => m is RequestResultMessage);
        var createdTask = frames.WaitAsync(m => m is SessionCreatedMessage);
        try
        {
            var reply = await stack.Channel.RequestAsync(new RequestCreateMessage(ControlRef.Next(), target.DeviceId, minutes), ReplyTimeout, ct);
            if (reply is ErrorMessage error)
            {
                // القناة المحاكية تعيد الإطار؛ الحقيقية ترمي. نتعامل مع الاثنين حتى تُسحب المحاكية.
                log.Emit("request.rejected_by_server", EventLog.Fields(("code", error.Code), ("message", error.Message)), $"request.create refused: {error.Code}");
                return new Pending(null, ExitCodes.ConnectFailed);
            }

            var createdReply = (RequestCreatedMessage)reply;
            log.Emit("request.created", EventLog.Fields(("request_id", createdReply.RequestId), ("expires_at", createdReply.ExpiresAt.ToString("O"))),
                $"request {createdReply.RequestId} pending until {createdReply.ExpiresAt:u}; waiting for the host to answer");
        }
        catch (ControlErrorException e)
        {
            log.Emit("request.rejected_by_server", EventLog.Fields(("code", e.Code), ("message", e.Message)), $"request.create refused: {e.Code} — {e.Message}");
            return new Pending(null, ExitCodes.ConnectFailed);
        }
        catch (TimeoutException)
        {
            log.Emit("request.no_reply", null, "no request.created within 15 s");
            return new Pending(null, ExitCodes.ConnectFailed);
        }

        var finished = await Task.WhenAny(resultTask, createdTask).WaitAsync(ct);
        if (finished == resultTask && (RequestResultMessage)await resultTask is { Accepted: false } refused)
        {
            log.Emit("request.result", EventLog.Fields(("request_id", refused.RequestId), ("accepted", false), ("reason", refused.Reason)),
                $"the host did not accept: {refused.Reason}");
            return new Pending(null, ExitCodes.ConnectFailed);
        }

        return new Pending((SessionCreatedMessage)await createdTask.WaitAsync(ReplyTimeout, ct), null);
    }

    /// <summary>معرّف الجهاز، أو تطابق تام على اسم الجهاز/المستخدم، ثم تطابق جزئي وحيد. الغموض = لا اختيار.</summary>
    public static HostInfoDto? ResolveHost(IReadOnlyList<HostInfoDto> hosts, string wanted)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var text = (wanted ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (Guid.TryParse(text, out var id)) return hosts.FirstOrDefault(h => h.DeviceId == id);

        var exact = hosts.Where(h =>
            string.Equals(h.DeviceName, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.UserDisplayName, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;

        var partial = hosts.Where(h =>
            h.DeviceName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            h.UserDisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    // ---------- الترجمة ----------

    public static int ExitFor(SessionOutcomeKind kind) => kind switch
    {
        SessionOutcomeKind.Terminated or SessionOutcomeKind.Expired or SessionOutcomeKind.LocalEnd => ExitCodes.Success,
        SessionOutcomeKind.ConnectFailed => ExitCodes.ConnectFailed,
        SessionOutcomeKind.TunnelDied or SessionOutcomeKind.ChannelLost => ExitCodes.TunnelDied,
        _ => ExitCodes.ProtocolOrAuth,
    };

    private static Dictionary<string, object?> Describe(ControlMessage message) => message switch
    {
        HostsMessage m => EventLog.Fields(("type", m.Type), ("count", m.Hosts.Count)),
        RequestIncomingMessage m => EventLog.Fields(("type", m.Type), ("request_id", m.RequestId), ("guest", m.GuestName), ("duration_min", m.DurationMin)),
        RequestResultMessage m => EventLog.Fields(("type", m.Type), ("request_id", m.RequestId), ("accepted", m.Accepted), ("reason", m.Reason)),
        RequestExpiredMessage m => EventLog.Fields(("type", m.Type), ("request_id", m.RequestId)),
        SessionCreatedMessage m => EventLog.Fields(("type", m.Type), ("session_id", m.SessionId), ("role", m.Role), ("same_public_ip", m.SamePublicIp)),
        SessionPeerEndpointMessage m => EventLog.Fields(("type", m.Type), ("session_id", m.SessionId), ("candidates", m.Candidates.Count)),
        SessionActiveMessage m => EventLog.Fields(("type", m.Type), ("session_id", m.SessionId), ("expires_at", m.ExpiresAt.ToString("O"))),
        SessionTerminateMessage m => EventLog.Fields(("type", m.Type), ("session_id", m.SessionId), ("reason", m.Reason)),
        AllowlistUpdatedMessage m => EventLog.Fields(("type", m.Type), ("version", m.Version)),
        ErrorMessage m => EventLog.Fields(("type", m.Type), ("code", m.Code), ("message", m.Message), ("ref", m.Ref)),
        _ => EventLog.Fields(("type", message.Type)),
    };

    private static string? Summarize(ControlMessage message) => message switch
    {
        // ping/pong كل 20 ثانية: في JSON نعم، على الشاشة لا.
        PingMessage or PongMessage => null,
        HostsMessage m => $"← {m.Type}: {m.Hosts.Count} host(s)",
        ErrorMessage m => $"← error {m.Code}: {m.Message}",
        SessionTerminateMessage m => $"← session.terminate: {m.Reason}",
        _ => $"← {message.Type}",
    };
}
