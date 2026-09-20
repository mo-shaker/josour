using Josour.Core.Control;
using Josour.Infrastructure.Control;
using Josour.Core.Tunnel;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Session;

namespace Josour.Spike;

/// <summary>
/// The headless equivalent of the WPF application: it drives the entire real stack (ApiClient -> AuthSession -> ControlChannel over WSS ->
/// SessionCoordinator -> TunnelSession -> Egress/Proxy) so QA can run a real session between two Windows machines.
///
/// <para>
/// The command is deliberately <b>a thin driver</b>: everything after <c>session.created</c> is owned by <see cref="SessionCoordinator"/> in
/// <c>Josour.Infrastructure</c> — the very code the WPF application drives — and the whole cleanup order is owned by <c>TunnelSession</c>.
/// What remains here is what belongs to the tool alone: parsing the arguments, signing in, <c>host.available</c>, the request cycle (automatic acceptance or
/// creation), and printing the events; and what belongs to the tool in building the tunnel and its events is in <see cref="SessionDriver"/>.
/// If this file grows session logic, its place is <c>SessionCoordinator</c> rather than here.
/// </para>
///
/// <para>The output: one JSON line per event on stdout (JSONL), and a human summary on stderr. The codes are in <see cref="ExitCodes"/>.</para>
/// </summary>
public static class SessionCommand
{
    public static class ExitCodes
    {
        /// <summary>The session worked and ended as expected (one side ending it, the duration running out, Ctrl+C), or <c>--list-hosts</c> succeeded.</summary>
        public const int Success = 0;

        /// <summary>Misuse: missing or contradictory arguments, or a requested host that does not exist.</summary>
        public const int Usage = 1;

        /// <summary>No tunnel stood up: the symmetric connection failed, or the request was rejected, or it timed out, or the host is unavailable.</summary>
        public const int ConnectFailed = 2;

        /// <summary>The tunnel stood up and then died (the other side dropping, or the control channel falling during the session).</summary>
        public const int TunnelDied = 3;

        /// <summary>An authentication failure or a breach of the contract by the server.</summary>
        public const int ProtocolOrAuth = 4;

        /// <summary>Ctrl+C before the session stood up.</summary>
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

        // ---------- 1. Signing in ----------
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

        // ---------- 2. The control channel ----------
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

        // The coordinator subscribes to the channel from now — before ConnectAsync — so it sees hello.ack (the allowed ports and the public IP)
        // and every frame after it with no gap, exactly as the application does at startup.
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
            var diagnostics = await Josour.Tunnel.Diagnostics.HostDiagnostics.CollectAsync(ct);
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

        // ---------- 3. The site list ----------
        // For the event alone: the coordinator loads the list itself at session.created at the session's version (the same parser and the same source).
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

        // ---------- 4. The role ----------
        // (The session.created event itself is emitted by SessionDriver from the inbound frame, before the coordinator starts preparing.)
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

        // ---------- 5. The session ----------
        // From here on nothing happens in this file: the coordinator saw session.created on the same channel and drives the rest.

        // Ctrl+C during the session = a deliberate end with the full cleanup order, not killing the process.
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

        await ReportListenerProbesAsync(stack, api, driver, log, args, roleText);

        var exit = ExitFor(outcome.Kind);
        log.Emit("tool.exit", EventLog.Fields(
            ("code", exit), ("kind", outcome.Kind.ToString()), ("end_reason", outcome.EndReason.ToString()),
            ("bytes_up", outcome.Stats.BytesUp), ("bytes_down", outcome.Stats.BytesDown), ("domains", outcome.Domains.ToList()),
            ("detail", outcome.Detail)),
            $"exit {exit}: {outcome.Kind} ({outcome.Detail ?? outcome.EndReason.ToString()}); {outcome.Stats.BytesUp} B up / {outcome.Stats.BytesDown} B down");
        return exit;
    }

    // ---------- The listener's security signal ----------

    /// <summary>
    /// It reports the unauthorised access attempts on the tunnel's listener to <c>POST /api/v1/diagnostics</c> at the session's end,
    /// under the keys reserved in <c>docs/api.md</c>. The tunnel's listener is the only place where the system sees such an attempt,
    /// and the server cannot observe it itself, so if the client does not report it, it is never observed.
    ///
    /// <para>Purely best effort: it does not throw, does not change the exit code, and its timeout is independent of the cancellation token, because the usual reason for reaching
    /// this point is Ctrl+C (so the token is already cancelled and the report is still wanted). It is disabled with <c>--no-diagnostics</c>.</para>
    /// </summary>
    private static async Task ReportListenerProbesAsync(SessionStack stack, string apiBase, SessionDriver driver, EventLog log, Args args, string role)
    {
        if (args.Has("no-diagnostics")) return;
        if (driver.ListenerDiagnostics() is not { } report) return;
        var (sessionId, data) = report;

        var count = data.TryGetValue("listener_unauthenticated", out var value) ? value : 0;
        var peers = data.TryGetValue("unauthenticated_peers", out var list) && list is IReadOnlyCollection<string> ips ? ips.Count : 0;
        log.Emit("listener.unauthenticated", EventLog.Fields(
            ("session_id", sessionId), ("count", count), ("distinct_peers_listed", peers),
            ("listener_port", data.TryGetValue("listener_port", out var port) ? port : null)),
            $"listener saw {count} connection(s) that never passed AUTH1 ({peers} source address(es) reported)");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var token = await stack.Auth.GetValidAccessTokenAsync(timeout.Token).ConfigureAwait(false);
            if (token is null)
            {
                log.Note("no access token at session end: the unauthenticated-listener report was not sent");
                return;
            }
            var posted = await DiagnosticsPoster.PostAsync(apiBase, token, sessionId, role, data, timeout.Token).ConfigureAwait(false);
            log.Emit("diagnostics.posted", EventLog.Fields(("kind", "listener_unauthenticated"), ("ok", posted)));
        }
        catch (Exception e)
        {
            log.Emit("diagnostics.posted", EventLog.Fields(("kind", "listener_unauthenticated"), ("ok", false), ("error", $"{e.GetType().Name}: {e.Message}")));
        }
    }

    // ---------- The host ----------

    private readonly record struct Pending(SessionCreatedMessage? Created, int? ExitCode);

    private static async Task<Pending> AwaitIncomingRequestAsync(SessionStack stack, FrameWatcher frames, EventLog log, Args args, CancellationToken ct)
    {
        var autoReject = args.Has("auto-reject");

        // host.available is the default (without it this machine appears in no user's list, so waiting is pointless);
        // --available is an explicit form of the default, and --no-available keeps the host connected and unannounced (testing the channel alone).
        var announce = !args.Has("no-available");
        // listen_port is optional: with a value the server runs the reachability probe (docs/ws-protocol.md section 6). We pass
        // the same port the tunnel's listener will bind (--listen-port) so the probe is meaningful.
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

    // ---------- The user ----------

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
            // The channel did not send the snapshot after hello.ack: we fall back to REST with the same shape and the same filtering (docs/api.md).
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
                // The simulated channel returns the frame; the real one throws. We handle both until the simulated one is withdrawn.
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

    /// <summary>The device id, or an exact match on the device/user name, then a single partial match. Ambiguity = no choice.</summary>
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

    // ---------- The translation ----------

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
        // ping/pong every 20 seconds: yes in the JSON, no on the screen.
        PingMessage or PongMessage => null,
        HostsMessage m => $"← {m.Type}: {m.Hosts.Count} host(s)",
        ErrorMessage m => $"← error {m.Code}: {m.Message}",
        SessionTerminateMessage m => $"← session.terminate: {m.Reason}",
        _ => $"← {message.Type}",
    };
}
