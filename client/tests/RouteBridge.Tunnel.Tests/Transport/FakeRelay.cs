using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Mux;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.Tunnel.Tests.Transport;

/// <summary>
/// Relay وهمي داخل العملية: يقرأ مقدمة <see cref="RelayProtocol"/> من كل طرف، يزاوج الطرفين بـ session_id، يرد
/// <see cref="RelayStatus.Paired"/> للاثنين ثم يضخ البايتات بينهما بلا تفسير. يكفي لإثبات أن جانب العميل صحيح؛
/// الخدمة الحقيقية (تحقق التوكن، الحدود، القياس) من عمل الأسبوع 5/6 إن أغلقت بوابة ADR-0003.
/// </summary>
internal sealed class FakeRelay : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Waiting>> _waiting = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly List<RelayPreamble> _received = new();
    private readonly object _gate = new();
    private readonly Task _acceptLoop;

    public FakeRelay()
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(16);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    /// <summary>حالة تُرد دائمًا بدل الاقتران (لاختبار الرفض).</summary>
    public RelayStatus? ForcedStatus { get; set; }

    /// <summary>توكن متوقَّع؛ أي غيره يرد unauthorized.</summary>
    public string? ExpectedToken { get; set; }

    public IReadOnlyList<RelayPreamble> Received
    {
        get { lock (_gate) return _received.ToArray(); }
    }

    public CandidateEndpoint Endpoint => new(CandidateType.Public, "127.0.0.1", Port);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptAsync(ct); }
            catch { break; }
            var task = Task.Run(() => HandleAsync(socket, ct), CancellationToken.None);
            _handlers[task] = 0;
            _ = task.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken ct)
    {
        var stream = new NetworkStream(socket, ownsSocket: true);
        RelayPreamble preamble;
        try
        {
            preamble = await RelayProtocol.ReadPreambleAsync(stream, TimeSpan.FromSeconds(5), ct);
        }
        catch
        {
            await ReplyAsync(stream, RelayStatus.ProtocolError, ct);
            await stream.DisposeAsync();
            return;
        }

        lock (_gate) _received.Add(preamble);

        var status = ForcedStatus
            ?? (ExpectedToken is not null && !string.Equals(ExpectedToken, preamble.Token, StringComparison.Ordinal)
                ? RelayStatus.Unauthorized
                : (RelayStatus?)null);
        if (status is { } rejected)
        {
            await ReplyAsync(stream, rejected, ct);
            await stream.DisposeAsync();
            return;
        }

        var slot = _waiting.GetOrAdd(preamble.SessionId, _ => new TaskCompletionSource<Waiting>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (slot.TrySetResult(new Waiting(preamble, stream)))
            return; // أول الواصلين: الثاني هو من يزاوج ويضخ؛ الـ stream يبقى حيًا في الفتحة

        var first = await slot.Task;
        _waiting.TryRemove(preamble.SessionId, out _);
        if (first.Preamble.Role == preamble.Role)
        {
            await ReplyAsync(stream, RelayStatus.Busy, ct);
            await stream.DisposeAsync();
            return;
        }

        await ReplyAsync(first.Stream, RelayStatus.Paired, ct);
        await ReplyAsync(stream, RelayStatus.Paired, ct);
        try { await StreamPump.RunAsync(first.Stream, stream, ct); }
        catch { /* أحد الطرفين أغلق */ }
        finally
        {
            await first.Stream.DisposeAsync();
            await stream.DisposeAsync();
        }
    }

    private static async Task ReplyAsync(Stream stream, RelayStatus status, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(RelayProtocol.BuildReply(status), ct);
            await stream.FlushAsync(ct);
        }
        catch { /* الطرف الآخر أغلق */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* تجاهل */ }
        try { await _acceptLoop; } catch { /* تجاهل */ }
        foreach (var waiting in _waiting.Values)
        {
            if (waiting.Task.IsCompletedSuccessfully) await waiting.Task.Result.Stream.DisposeAsync();
        }
        try { await Task.WhenAll(_handlers.Keys); } catch { /* المعالجات تبتلع أخطاءها */ }
        _listener.Dispose();
        _cts.Dispose();
    }

    private sealed record Waiting(RelayPreamble Preamble, Stream Stream);
}
