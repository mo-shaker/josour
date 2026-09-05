using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Nerdbank.Streams;
using RouteBridge.Core.Tunnel;

namespace RouteBridge.Tunnel.Mux;

public sealed class MuxOptions
{
    /// <summary>
    /// تجاوز صريح لنافذة الاستقبال لكل stream بالبايت. <c>null</c> (الافتراضي) = تُشتق من الـ RTT المقيس عند
    /// الاتصال (<see cref="MuxWindow.ForRoundTrip"/>) — يفعل ذلك <c>TunnelSession</c> لأنه وحده يعرف
    /// <c>connect_ms</c> — وتصير <see cref="MuxWindow.Default"/> (1 MiB) عند إنشاء Mux مباشرةً بلا اشتقاق.
    /// </summary>
    public int? ReceiveWindow { get; init; }

    /// <summary>تجاوز صريح لحد الـ streams المتزامنة. <c>null</c> = يتبع النافذة (ميزانية 256 MiB).</summary>
    public int? MaxConcurrentStreams { get; init; }

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>أقصى انتظار لـ OPEN_OK/OPEN_FAIL (المضيف: DNS 5 ث + اتصال 10 ث).</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public bool EnableLiveness { get; init; } = true;
    /// <summary>تتبع Nerdbank الداخلي (معايير ADR-0006: سهولة التتبع عند الأعطال).</summary>
    public TraceSource? Trace { get; init; }

    /// <summary>
    /// النافذة الفعلية: التجاوز الصريح إن وُجد، وإلا <paramref name="derived"/> المشتقة من الـ RTT،
    /// وإلا <see cref="MuxWindow.Default"/>. التجاوز يفوز دائمًا (الاختبارات وأداة Spike تعتمد عليه).
    /// </summary>
    public MuxWindow Resolve(MuxWindow? derived = null)
    {
        var window = ReceiveWindow is int bytes
            ? MuxWindow.ForWindow(bytes, reason: $"explicit MuxOptions.ReceiveWindow {bytes} bytes")
            : derived ?? MuxWindow.Default;
        return MaxConcurrentStreams is int max ? window.WithMaxConcurrentStreams(max) : window;
    }
}

/// <summary>
/// ADR-0006: Nerdbank.Streams.MultiplexingStream (بروتوكول 3، بلا مصافحة) خلف IMuxConnection/IMuxAcceptor.
/// - OPEN: Guest يعرض قناة باسم "host:port". المضيف يقبلها دائمًا ثم يكتب بايت حالة واحدًا: 0 = OPEN_OK ويبدأ الضخ،
///   وإلا رمز OPEN_FAIL (1..7) ثم يكمل الكتابة. الرفض داخل القناة نفسها يضمن الترتيب ولا يحتاج قناة تحكم لكل فتح.
/// - PING/PONG/GOAWAY على قناة مزروعة (seeded, id 0) بإطارات 9 بايت: u8 type | 8 بايت حمولة.
/// - نافذة الاستقبال لكل قناة تُشتق من الـ RTT (<see cref="MuxWindow"/>) وتُعلَن للطرف الآخر في إطار العرض/القبول
///   (Backpressure من Nerdbank عبر System.IO.Pipelines).
/// </summary>
public sealed class NerdbankMux : IMuxConnection, IMuxAcceptor
{
    private const byte StatusOk = 0;
    private const byte CtlPing = 1;
    private const byte CtlPong = 2;
    private const byte CtlGoAway = 3;
    private const int CtlFrameLength = 9;

    /// <summary>
    /// القناة المزروعة لا تمر بعرض/قبول، فلا تُعلَن نافذتها على السلك: يفترض كل طرف أن نافذة الآخر تساوي نافذته.
    /// لذلك تُثبَّت فوق أكبر شريحة ممكنة حتى تبقى القيمة نفسها عند الطرفين مهما اختلفت شريحتاهما
    /// (Nerdbank يرفع أي <c>ChannelReceivingWindowSize</c> أصغر من الافتراضية إلى الافتراضية).
    /// رصيد لا حجز: القناة لا تحمل إلا إطارات 9 بايت.
    /// </summary>
    private const int ControlChannelWindow = MuxWindow.DistantWindow;

    private static readonly TimeSpan GoAwayFlushTimeout = TimeSpan.FromSeconds(2);
    /// <summary>بعد GOAWAY ننتظر إغلاق الطرف الآخر (أو هذه المهلة) قبل إسقاط النقل، حتى لا يضيع الإطار في المخازن.</summary>
    private static readonly TimeSpan GoAwayDrainTimeout = TimeSpan.FromMilliseconds(500);

    private readonly MultiplexingStream _mx;
    private readonly CountingStream _transport;
    private readonly MuxOptions _options;
    private readonly MuxWindow _window;
    private readonly TunnelRole _role;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<long>> _pendingPings = new();
    private readonly SemaphoreSlim _controlWriteGate = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastPongMs;
    private int _openStreams;
    private int _closed;
    private Task _controlLoop = Task.CompletedTask;
    private Task _livenessLoop = Task.CompletedTask;
    private MultiplexingStream.Channel _control = null!;

    private NerdbankMux(MultiplexingStream mx, CountingStream transport, TunnelRole role, MuxOptions options, MuxWindow window)
    {
        _mx = mx;
        _transport = transport;
        _role = role;
        _options = options;
        _window = window;
        _lastPongMs = 0;
    }

    public TunnelRole Role => _role;

    /// <summary>النافذة المطبَّقة فعلًا وحد الـ streams المرافق لها (docs/protocol.md القسم 5).</summary>
    public MuxWindow Window => _window;
    public Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
    public MuxStats Stats => new(_transport.BytesWritten, _transport.BytesRead, Volatile.Read(ref _openStreams));
    public Task Completion => _completion.Task;
    public GoAwayReason? RemoteGoAway { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// ينشئ الـ Mux فوق الـ stream المصادَق (SslStream من SymmetricConnector) ويبدأ الاستماع فورًا. يملك الـ stream.
    /// </summary>
    /// <param name="window">
    /// النافذة المشتقة من الـ RTT (<c>TunnelSession</c> يمررها بعد <c>SymmetricConnector</c>). null = ما تعطيه
    /// <see cref="MuxOptions.Resolve"/> وحدها: التجاوز الصريح إن وُجد وإلا <see cref="MuxWindow.Default"/>.
    /// </param>
    public static NerdbankMux Create(Stream authenticatedStream, TunnelRole role, MuxOptions? options = null, MuxWindow? window = null)
    {
        ArgumentNullException.ThrowIfNull(authenticatedStream);
        options ??= new MuxOptions();
        var effective = window ?? options.Resolve();
        var transport = new CountingStream(authenticatedStream);
        var mxOptions = new MultiplexingStream.Options
        {
            ProtocolMajorVersion = 3,
            DefaultChannelReceivingWindowSize = effective.ReceiveWindow,
            StartSuspended = true,
            TraceSource = options.Trace ?? new TraceSource("RouteBridge.Mux", SourceLevels.Off),
        };
        mxOptions.SeededChannels.Add(new MultiplexingStream.ChannelOptions { ChannelReceivingWindowSize = ControlChannelWindow });

        var mx = MultiplexingStream.Create(transport, mxOptions);
        try
        {
            var mux = new NerdbankMux(mx, transport, role, options, effective);
            mx.ChannelOffered += mux.OnChannelOffered;
            mx.StartListening();
            // القناة المزروعة (id 0) لا تُقبل إلا بعد بدء الاستماع.
            mux._control = mx.AcceptChannel(0);
            mux.Start();
            return mux;
        }
        catch
        {
            mx.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private void Start()
    {
        _controlLoop = Task.Run(ControlLoopAsync);
        if (_options.EnableLiveness) _livenessLoop = Task.Run(LivenessLoopAsync);
        _ = _mx.Completion.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Fail(new MuxClosedException("multiplexing stream faulted", t.Exception?.GetBaseException()));
                return;
            }

            // اكتمال بلا خطأ ليس دليل إغلاق نظيف. النظيف هو ما قررناه نحن (CloseAsync/Dispose)
            // أو ما تلا GOAWAY من الطرف الآخر؛ وكلاهما يضبط _closed قبل الوصول إلى هنا.
            // أما انتهاء النقل من تلقائه (انقطاع الشبكة أو موت عملية الطرف الآخر) فهو نفق ميت
            // يجب أن يظهر عطلًا، وإلا بدا للتطبيق كإنهاء عادي وأبلغ الخادم بسبب خاطئ.
            if (Volatile.Read(ref _closed) != 0 || RemoteGoAway is not null) Finish();
            else Fail(new MuxClosedException("transport closed without GOAWAY; tunnel is dead"));
        }, TaskScheduler.Default);
    }

    // ---------- Guest ----------

    public async Task<MuxOpenResult> OpenStreamAsync(string host, int port, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (host.Contains(':')) throw new ArgumentException("host must not contain ':' (IPv6 literals are rejected by policy)", nameof(host));
        if (IsClosed) throw new MuxClosedException("mux is closed");

        // ميزانية ذاكرة هذا الطرف: النافذة × الـ streams المتزامنة (docs/protocol.md القسم 5). المضيف يفرض حده
        // على السلك في StreamLimiter، وهذا الحجز يحمي ذاكرة الضيف نفسه إن اختلفت شريحته عن شريحة المضيف
        // (كل طرف يشتق نافذته من قياسه هو). الحجز قبل العرض لا بعده حتى لا تتجاوزه دفعة فتوحات متوازية.
        if (Interlocked.Increment(ref _openStreams) > _window.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _openStreams);
            return MuxOpenResult.Fail(OpenFailReason.Limit);
        }

        var handedOff = false; // صار الـ stream ملك المتصل، وهو الذي يحرر المكان عند التخلص منه
        try
        {
            return await OpenReservedAsync(host, port, () => handedOff = true, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff) Interlocked.Decrement(ref _openStreams);
        }
    }

    private async Task<MuxOpenResult> OpenReservedAsync(string host, int port, Action handOff, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        cts.CancelAfter(_options.OpenTimeout);
        MultiplexingStream.Channel channel;
        try
        {
            channel = await _mx.OfferChannelAsync(host + ":" + port.ToString(CultureInfo.InvariantCulture), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (IsClosed) throw new MuxClosedException("mux closed while opening a stream");
            return MuxOpenResult.Fail(OpenFailReason.ConnectFailed); // مهلة OPEN
        }
        catch (Exception e) when (e is not MuxClosedException)
        {
            if (IsClosed) throw new MuxClosedException("mux closed while opening a stream", e);
            return MuxOpenResult.Fail(OpenFailReason.ConnectFailed); // رفض على مستوى Nerdbank (لا يحدث في بروتوكولنا)
        }

        try
        {
            var status = await ReadStatusAsync(channel.Input, cts.Token).ConfigureAwait(false);
            if (status == StatusOk)
            {
                var stream = new PipeDuplexStream(channel.Input, channel.Output, channel.Completion, channel.Dispose, () => Interlocked.Decrement(ref _openStreams));
                handOff();
                return MuxOpenResult.Ok(stream);
            }

            channel.Input.Complete();
            channel.Output.Complete();
            var reason = (OpenFailReason)status;
            return MuxOpenResult.Fail(MuxWire.IsValid(reason) ? reason : OpenFailReason.ConnectFailed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            channel.Dispose();
            throw;
        }
        catch (Exception)
        {
            channel.Dispose();
            if (IsClosed) throw new MuxClosedException("mux closed while opening a stream");
            return MuxOpenResult.Fail(OpenFailReason.ConnectFailed);
        }
    }

    private static async Task<byte> ReadStatusAsync(System.IO.Pipelines.PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= 1)
            {
                var status = buffer.FirstSpan[0];
                reader.AdvanceTo(buffer.GetPosition(1));
                return status;
            }
            if (result.IsCompleted || result.IsCanceled)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("channel closed before OPEN status");
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    // ---------- Host ----------

    private void OnChannelOffered(object? sender, MultiplexingStream.ChannelOfferEventArgs e)
    {
        if (e.IsAccepted) return;
        MultiplexingStream.Channel channel;
        try
        {
            channel = _mx.AcceptChannel(e.QualifiedId.Id);
        }
        catch (Exception)
        {
            return; // أُغلقت أو قُبلت في مكان آخر
        }
        _ = Task.Run(() => HandleOfferAsync(channel, e.Name));
    }

    private async Task HandleOfferAsync(MultiplexingStream.Channel channel, string name)
    {
        Interlocked.Increment(ref _openStreams);
        try
        {
            var decision = await DecideAsync(name).ConfigureAwait(false);
            if (decision.Target is null)
            {
                var reason = decision.Reason ?? OpenFailReason.ConnectFailed;
                await WriteStatusAsync(channel, (byte)reason, _cts.Token).ConfigureAwait(false);
                channel.Output.Complete();
                channel.Input.Complete();
                try { await channel.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { channel.Dispose(); }
                return;
            }

            await using var target = decision.Target;
            await using var muxStream = new PipeDuplexStream(channel.Input, channel.Output, channel.Completion, channel.Dispose);
            await WriteStatusAsync(channel, StatusOk, _cts.Token).ConfigureAwait(false);
            await StreamPump.RunAsync(muxStream, target, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { channel.Dispose(); } catch { /* تجاهل */ }
        }
        finally
        {
            Interlocked.Decrement(ref _openStreams);
        }
    }

    private async Task<MuxOpenDecision> DecideAsync(string name)
    {
        if (!TryParseName(name, out var host, out var port)) return MuxOpenDecision.Fail(OpenFailReason.NotAllowed);
        var handler = OpenRequested;
        if (handler is null) return MuxOpenDecision.Fail(OpenFailReason.NotAllowed);
        try
        {
            return await handler(new MuxOpenRequest(host, port), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return MuxOpenDecision.Fail(OpenFailReason.ConnectFailed);
        }
    }

    /// <summary>"host:port" حيث host بلا ':' (لا IPv6 حرفي) والمنفذ 1..65535.</summary>
    public static bool TryParseName(string name, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(name) || name.Length > 300) return false;
        var colon = name.LastIndexOf(':');
        if (colon <= 0 || colon == name.Length - 1) return false;
        if (name.IndexOf(':') != colon) return false;
        if (!int.TryParse(name.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535) return false;
        host = name[..colon];
        return true;
    }

    private static async Task WriteStatusAsync(MultiplexingStream.Channel channel, byte status, CancellationToken ct)
    {
        var writer = channel.Output;
        writer.GetSpan(1)[0] = status;
        writer.Advance(1);
        var flush = await writer.FlushAsync(ct).ConfigureAwait(false);
        if (flush.IsCompleted) throw new IOException("channel closed before OPEN status was sent");
    }

    // ---------- control channel: PING/PONG/GOAWAY ----------

    public async Task<TimeSpan> PingAsync(CancellationToken ct)
    {
        if (IsClosed) throw new MuxClosedException("mux is closed");
        var nonce = RandomNumberGenerator.GetBytes(8);
        var key = BitConverter.ToUInt64(nonce);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPings[key] = tcs;
        var started = _clock.ElapsedMilliseconds;
        try
        {
            await SendControlAsync(CtlPing, nonce, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var pongAt = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            return TimeSpan.FromMilliseconds(Math.Max(0, pongAt - started));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MuxClosedException("mux closed while waiting for PONG");
        }
        finally
        {
            _pendingPings.TryRemove(key, out _);
        }
    }

    private async Task ControlLoopAsync()
    {
        var reader = _control.Input;
        var frame = new byte[CtlFrameLength];
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (buffer.Length >= CtlFrameLength)
                {
                    buffer.Slice(0, CtlFrameLength).CopyTo(frame);
                    buffer = buffer.Slice(CtlFrameLength);
                    await ProcessControlFrameAsync(frame).ConfigureAwait(false);
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted || result.IsCanceled) break;
            }
            if (RemoteGoAway is null && !IsClosed) Fail(new MuxClosedException("control channel closed by peer"));
            else Finish();
        }
        catch (OperationCanceledException)
        {
            Finish();
        }
        catch (Exception e)
        {
            Fail(new MuxClosedException("control channel failed", e));
        }
    }

    private async Task ProcessControlFrameAsync(byte[] frame)
    {
        switch (frame[0])
        {
            case CtlPing:
                await SendControlAsync(CtlPong, frame.AsMemory(1, 8).ToArray(), _cts.Token).ConfigureAwait(false);
                break;
            case CtlPong:
                Volatile.Write(ref _lastPongMs, _clock.ElapsedMilliseconds);
                if (_pendingPings.TryRemove(BitConverter.ToUInt64(frame, 1), out var tcs)) tcs.TrySetResult(_clock.ElapsedMilliseconds);
                break;
            case CtlGoAway:
            {
                var reason = (GoAwayReason)frame[1];
                RemoteGoAway = MuxWire.IsValid(reason) ? reason : GoAwayReason.ProtocolError;
                _ = Task.Run(() => ShutdownAsync(sendGoAway: false, GoAwayReason.SessionEnd));
                break;
            }
            default:
                RemoteGoAway = GoAwayReason.ProtocolError;
                _ = Task.Run(() => ShutdownAsync(sendGoAway: true, GoAwayReason.ProtocolError));
                break;
        }
    }

    private async Task SendControlAsync(byte type, ReadOnlyMemory<byte> payload8, CancellationToken ct)
    {
        await _controlWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var writer = _control.Output;
            WriteControlFrame(writer, type, payload8.Span);
            var flush = await writer.FlushAsync(ct).ConfigureAwait(false);
            if (flush.IsCompleted) throw new MuxClosedException("control channel closed");
        }
        finally
        {
            _controlWriteGate.Release();
        }
    }

    private static void WriteControlFrame(System.IO.Pipelines.PipeWriter writer, byte type, ReadOnlySpan<byte> payload8)
    {
        var span = writer.GetSpan(CtlFrameLength);
        span[0] = type;
        span.Slice(1, 8).Clear();
        payload8[..Math.Min(8, payload8.Length)].CopyTo(span[1..]);
        writer.Advance(CtlFrameLength);
    }

    private async Task LivenessLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_options.PingInterval, _cts.Token).ConfigureAwait(false);
                var lastPong = Volatile.Read(ref _lastPongMs);
                var sinceLast = _clock.ElapsedMilliseconds - Math.Max(lastPong, 0);
                if (lastPong > 0 && sinceLast > _options.DeadAfter.TotalMilliseconds)
                {
                    Fail(new MuxClosedException($"no PONG for {sinceLast} ms; tunnel is dead"));
                    return;
                }
                _ = PingAsync(_cts.Token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                if (lastPong == 0) Volatile.Write(ref _lastPongMs, _clock.ElapsedMilliseconds); // بداية العد من أول PING
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- shutdown ----------

    public Task CloseAsync(GoAwayReason reason) => ShutdownAsync(sendGoAway: true, reason);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(sendGoAway: true, GoAwayReason.SessionEnd).ConfigureAwait(false);
        _cts.Dispose();
        _controlWriteGate.Dispose();
    }

    private async Task ShutdownAsync(bool sendGoAway, GoAwayReason reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (sendGoAway)
        {
            try
            {
                await SendControlAsync(CtlGoAway, new[] { (byte)reason }, CancellationToken.None).WaitAsync(GoAwayFlushTimeout).ConfigureAwait(false);
                // FlushAsync يعني أن الإطار في أنبوب القناة لا على السلك؛ الطرف الآخر يغلق النقل عند استلام GOAWAY فننتظره (بحد أقصى).
                await Task.WhenAny(_control.Completion, _mx.Completion, Task.Delay(GoAwayDrainTimeout)).ConfigureAwait(false);
            }
            catch { /* النفق ميت أصلًا */ }
        }
        _cts.Cancel();
        try { await _mx.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل */ }
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل */ }
        foreach (var pending in _pendingPings.Values) pending.TrySetCanceled();
        Finish();
    }

    private void Fail(Exception e)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            // إغلاق مقصود جارٍ أو مكتمل (CloseAsync/GOAWAY): ما يظهر بعده من أخطاء قراءة ليس عطلًا.
            _completion.TrySetResult();
            return;
        }
        // السبب يُثبَّت على المهمة **قبل** إعلان الإغلاق، لسببين:
        //  1) _cts.Cancel() يوقظ حلقة التحكم التي تنهي المهمة بنجاح.
        //  2) موت النقل يوقظ مسارين معًا (نهاية Completion وحلقة التحكم): لو أعلن أحدهما الإغلاق أولًا
        //     لرأى الآخر IsClosed=true فأنهى المهمة بـ Finish، وTrySetResult تسبق TrySetException،
        //     فيظهر نفق ميت في صورة إغلاق نظيف (فلا حدث Died، ويُبلَّغ الخادم بسبب إنهاء خاطئ).
        _completion.TrySetException(e);
        // ومن يعلن الإغلاق فعلًا يتولى التنظيف مرة واحدة؛ إن سبقنا إغلاق مقصود فهو صاحبه.
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _ = _mx.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
        try { _transport.Dispose(); } catch { /* تجاهل */ }
        foreach (var pending in _pendingPings.Values) pending.TrySetCanceled();
    }

    private void Finish() => _completion.TrySetResult();
}
