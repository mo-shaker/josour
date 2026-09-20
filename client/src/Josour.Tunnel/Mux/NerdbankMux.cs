using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Nerdbank.Streams;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Mux;

public sealed class MuxOptions
{
    /// <summary>
    /// An explicit override of the per-stream receiving window in bytes. <c>null</c> (the default) = it is derived from the RTT
    /// measured at connect time (<see cref="MuxWindow.ForRoundTrip"/>) — which <c>TunnelSession</c> does, because it alone knows
    /// <c>connect_ms</c> — and it becomes <see cref="MuxWindow.Default"/> (1 MiB) when a mux is created directly with no derivation.
    /// </summary>
    public int? ReceiveWindow { get; init; }

    /// <summary>An explicit override of the concurrent stream limit. <c>null</c> = it follows the window (a 256 MiB budget).</summary>
    public int? MaxConcurrentStreams { get; init; }

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The longest wait for OPEN_OK/OPEN_FAIL (at the host: DNS 5 s + connect 10 s).</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public bool EnableLiveness { get; init; } = true;
    /// <summary>Nerdbank's internal tracing (an ADR-0006 criterion: ease of tracing when things break).</summary>
    public TraceSource? Trace { get; init; }

    /// <summary>
    /// The window actually applied: the explicit override if there is one, otherwise the RTT-derived <paramref name="derived"/>,
    /// otherwise <see cref="MuxWindow.Default"/>. The override always wins (the tests and the Spike tool rely on it).
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
/// ADR-0006: Nerdbank.Streams.MultiplexingStream (protocol 3, with no handshake) behind IMuxConnection/IMuxAcceptor.
/// - OPEN: the guest offers a channel named "host:port". The host always accepts it and then writes one status byte: 0 = OPEN_OK and pumping begins,
///   otherwise the OPEN_FAIL code (1..7), and then it finishes writing. Refusing inside the channel itself guarantees ordering and needs no control channel per open.
/// - PING/PONG/GOAWAY on a seeded channel (id 0) with 9-byte frames: u8 type | 8 payload bytes.
/// - The per-channel receiving window is derived from the RTT (<see cref="MuxWindow"/>) and announced to the other side in the offer/accept frame
///   (backpressure from Nerdbank through System.IO.Pipelines).
/// </summary>
public sealed class NerdbankMux : IMuxConnection, IMuxAcceptor
{
    private const byte StatusOk = 0;
    private const byte CtlPing = 1;
    private const byte CtlPong = 2;
    private const byte CtlGoAway = 3;
    private const int CtlFrameLength = 9;

    /// <summary>
    /// The seeded channel goes through no offer/accept, so its window is not announced on the wire: each side assumes the other's window equals its own.
    /// So it is pinned above the largest possible band, to keep the value the same at both ends however their bands differ
    /// (Nerdbank raises any <c>ChannelReceivingWindowSize</c> smaller than the default up to the default).
    /// Credit, not a reservation: the channel carries nothing but 9-byte frames.
    /// </summary>
    private const int ControlChannelWindow = MuxWindow.DistantWindow;

    private static readonly TimeSpan GoAwayFlushTimeout = TimeSpan.FromSeconds(2);
    /// <summary>After a GOAWAY we wait for the other side to close (or for this timeout) before dropping the transport, so the frame is not lost in the buffers.</summary>
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

    /// <summary>The window actually applied and the stream limit that goes with it (docs/protocol.md section 5).</summary>
    public MuxWindow Window => _window;
    public Func<MuxOpenRequest, CancellationToken, Task<MuxOpenDecision>>? OpenRequested { get; set; }
    public MuxStats Stats => new(_transport.BytesWritten, _transport.BytesRead, Volatile.Read(ref _openStreams));
    public Task Completion => _completion.Task;
    public GoAwayReason? RemoteGoAway { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// The number of PINGs outstanding with no PONG. Diagnostics, not behaviour: the soak test watches it because every outstanding one holds
    /// a <c>TaskCompletionSource</c> and a cancellation registration on <c>_cts</c>, so its growth = a leak.
    /// </summary>
    public int PendingPings => _pendingPings.Count;

    /// <summary>
    /// Creates the mux over the authenticated stream (the SslStream from SymmetricConnector) and starts listening at once. It owns the stream.
    /// </summary>
    /// <param name="window">
    /// The RTT-derived window (<c>TunnelSession</c> passes it after <c>SymmetricConnector</c>). null = whatever
    /// <see cref="MuxOptions.Resolve"/> gives on its own: the explicit override if there is one, otherwise <see cref="MuxWindow.Default"/>.
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
            TraceSource = options.Trace ?? new TraceSource("Josour.Mux", SourceLevels.Off),
        };
        mxOptions.SeededChannels.Add(new MultiplexingStream.ChannelOptions { ChannelReceivingWindowSize = ControlChannelWindow });

        var mx = MultiplexingStream.Create(transport, mxOptions);
        try
        {
            var mux = new NerdbankMux(mx, transport, role, options, effective);
            mx.ChannelOffered += mux.OnChannelOffered;
            mx.StartListening();
            // The seeded channel (id 0) is only accepted once listening has started.
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
        // Completion may be watched by nobody: TunnelSession attaches WatchAsync but there are paths before it (the role-specific end
        // failing to build, so an immediate dispose) and uses with no TunnelSession at all (the Spike tool, the tests). Without this silent
        // observer every tunnel death becomes an unobserved task exception raised by the finaliser thread — noise today, and a process crash on any
        // host that enables ThrowUnobservedTaskExceptions. The real observers are unaffected: they all read the same task.
        _ = _completion.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _controlLoop = Task.Run(ControlLoopAsync);
        if (_options.EnableLiveness) _livenessLoop = Task.Run(LivenessLoopAsync);
        _ = _mx.Completion.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Fail(new MuxClosedException("multiplexing stream faulted", t.Exception?.GetBaseException()));
                return;
            }

            // Completing with no error is not proof of a clean close. A clean one is what we decided (CloseAsync/Dispose)
            // or what followed a GOAWAY from the other side; both set _closed before reaching here.
            // The transport ending of its own accord (a network drop or the other side's process dying) is a dead tunnel
            // that must surface as a failure, otherwise it looks to the application like an ordinary end and the server is told a wrong reason.
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

        // This side's memory budget: the window x the concurrent streams (docs/protocol.md section 5). The host enforces its limit
        // on the wire in StreamLimiter, and this reservation protects the guest's own memory if its band differs from the host's
        // (each side derives its window from its own measurement). The reservation comes before the offer, not after, so a burst of parallel opens cannot exceed it.
        if (Interlocked.Increment(ref _openStreams) > _window.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _openStreams);
            return MuxOpenResult.Fail(OpenFailReason.Limit);
        }

        var handedOff = false; // the stream became the caller's, and the caller is what frees the slot when disposing it
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
            return MuxOpenResult.Fail(OpenFailReason.ConnectFailed); // the OPEN timeout
        }
        catch (Exception e) when (e is not MuxClosedException)
        {
            if (IsClosed) throw new MuxClosedException("mux closed while opening a stream", e);
            return MuxOpenResult.Fail(OpenFailReason.ConnectFailed); // a rejection at the Nerdbank level (does not happen in our protocol)
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
            return; // closed, or accepted somewhere else
        }
        _ = Task.Run(() => HandleOfferAsync(channel, e.Name));
    }

    private async Task HandleOfferAsync(MultiplexingStream.Channel channel, string name)
    {
        // The band's limit on the acceptance path too, not on the open path alone: the other side is what decides how many channels are offered,
        // so without this limit a hostile peer could force us to create channels (and their pipes) with no ceiling. The host has a finer limit in
        // Josour.Egress.StreamLimiter (which includes 50 opens/second) and it comes first; this protects both sides, and the guest has no other limit.
        if (Interlocked.Increment(ref _openStreams) > _window.MaxConcurrentStreams)
        {
            try
            {
                await WriteStatusAsync(channel, (byte)OpenFailReason.Limit, _cts.Token).ConfigureAwait(false);
                channel.Output.Complete();
                channel.Input.Complete();
            }
            catch (Exception)
            {
                try { channel.Dispose(); } catch { /* ignore */ }
            }
            finally
            {
                Interlocked.Decrement(ref _openStreams);
            }
            return;
        }

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
            try { channel.Dispose(); } catch { /* ignore */ }
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

    /// <summary>"host:port" where the host contains no ':' (no IPv6 literal) and the port is 1..65535.</summary>
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
                var terminal = false;
                while (!terminal && buffer.Length >= CtlFrameLength)
                {
                    buffer.Slice(0, CtlFrameLength).CopyTo(frame);
                    buffer = buffer.Slice(CtlFrameLength);
                    terminal = !await ProcessControlFrameAsync(frame).ConfigureAwait(false);
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                // A terminating frame (GOAWAY or a protocol error): whoever handled it set the final state, and there is no point reading past it.
                if (terminal) return;
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

    /// <summary>Handles one control frame. <c>false</c> = a terminating frame (GOAWAY or a protocol error), so the control loop stops.</summary>
    private async Task<bool> ProcessControlFrameAsync(byte[] frame)
    {
        switch (frame[0])
        {
            case CtlPing:
                await SendControlAsync(CtlPong, frame.AsMemory(1, 8).ToArray(), _cts.Token).ConfigureAwait(false);
                return true;

            case CtlPong:
                Volatile.Write(ref _lastPongMs, _clock.ElapsedMilliseconds);
                // An unknown nonce (a duplicated or forged PONG) is dropped with no effect: nothing is added to _pendingPings from the wire.
                if (_pendingPings.TryRemove(BitConverter.ToUInt64(frame, 1), out var tcs)) tcs.TrySetResult(_clock.ElapsedMilliseconds);
                return true;

            case CtlGoAway:
            {
                var raw = frame[1];
                var reason = (GoAwayReason)raw;
                if (!MuxWire.IsValid(reason))
                {
                    // A reason outside the contract: treated as a protocol error rather than as a clean close.
                    RemoteGoAway = GoAwayReason.ProtocolError;
                    _ = FailProtocolAsync($"GOAWAY carried an unknown reason {raw}", sendGoAway: true);
                    return false;
                }

                RemoteGoAway = reason;
                if (reason == GoAwayReason.ProtocolError)
                {
                    // The other side says the wire broke: an abnormal end the application must report
                    // (TunnelSession.SuggestDeathReason => TunnelEndReason.ProtocolError), not a silent clean close.
                    _ = FailProtocolAsync("peer sent GOAWAY(protocol_error)", sendGoAway: false);
                    return false;
                }

                _ = Task.Run(() => ShutdownAsync(sendGoAway: false, GoAwayReason.SessionEnd));
                return false;
            }

            default:
                RemoteGoAway = GoAwayReason.ProtocolError;
                _ = FailProtocolAsync($"unknown control frame type 0x{frame[0]:x2}", sendGoAway: true);
                return false;
        }
    }

    /// <summary>
    /// A protocol error on the control channel: GOAWAY(protocol_error) if we are the ones who found it, then an explicit <b>fault</b> on
    /// <see cref="Completion"/>. What matters is that the failure surfaces as a failure: a clean close does not raise <c>Died</c> in
    /// <c>TunnelSession</c>, so a hostile peer corrupting the control channel ended the session with no reason and the server was told of an ordinary end.
    /// </summary>
    private async Task FailProtocolAsync(string detail, bool sendGoAway)
    {
        if (sendGoAway && Volatile.Read(ref _closed) == 0)
        {
            try
            {
                await SendControlAsync(CtlGoAway, new[] { (byte)GoAwayReason.ProtocolError }, CancellationToken.None)
                    .WaitAsync(GoAwayFlushTimeout).ConfigureAwait(false);
                await Task.WhenAny(_control.Completion, _mx.Completion, Task.Delay(GoAwayDrainTimeout)).ConfigureAwait(false);
            }
            catch { /* the tunnel is already dead */ }
        }
        Fail(new MuxClosedException($"control channel protocol error: {detail}"));
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
                _ = LivenessPingAsync();
                if (lastPong == 0) Volatile.Write(ref _lastPongMs, _clock.ElapsedMilliseconds); // start counting from the first PING
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The liveness PING: it gives up waiting after <see cref="MuxOptions.DeadAfter"/> instead of staying outstanding until the tunnel dies.
    /// Without that bound, every PING with no PONG left an entry in <c>_pendingPings</c> and a cancellation registration on <c>_cts</c> that lived
    /// as long as the tunnel, so both grew without bound whenever the PONG was late without the link dropping. The result here is not read: judging death
    /// is the loop's own job through <c>_lastPongMs</c>, and all that matters is that the outstanding entry is released.
    /// </summary>
    private async Task LivenessPingAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(_options.DeadAfter);
            await PingAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation, or a timeout, or a closed tunnel: all of them observed here so none becomes an unobserved exception.
        }
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
                // FlushAsync means the frame is in the channel's pipe rather than on the wire; the other side closes the transport on receiving a GOAWAY, so we wait for it (with a ceiling).
                await Task.WhenAny(_control.Completion, _mx.Completion, Task.Delay(GoAwayDrainTimeout)).ConfigureAwait(false);
            }
            catch { /* the tunnel is already dead */ }
        }
        _cts.Cancel();
        try { await _mx.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        foreach (var pending in _pendingPings.Values) pending.TrySetCanceled();
        Finish();
    }

    private void Fail(Exception e)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            // A deliberate close is under way or complete (CloseAsync/GOAWAY): read errors appearing after it are not a failure.
            _completion.TrySetResult();
            return;
        }
        // The cause is set on the task **before** the close is announced, for two reasons:
        //  1) _cts.Cancel() wakes the control loop, which completes the task successfully.
        //  2) The transport dying wakes two paths at once (Completion ending and the control loop): if one announced the close first,
        //     the other would see IsClosed=true and finish the task with Finish, and TrySetResult precedes TrySetException,
        //     so a dead tunnel would appear as a clean close (no Died event, and the server told a wrong end reason).
        _completion.TrySetException(e);
        // And whoever actually announces the close performs the cleanup once; if a deliberate close got there first, it owns it.
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _ = _mx.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
        try { _transport.Dispose(); } catch { /* ignore */ }
        foreach (var pending in _pendingPings.Values) pending.TrySetCanceled();
    }

    private void Finish() => _completion.TrySetResult();
}
