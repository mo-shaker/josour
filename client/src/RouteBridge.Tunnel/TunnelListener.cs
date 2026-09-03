using System.Net;
using System.Net.Sockets;

namespace RouteBridge.Tunnel;

/// <summary>
/// مستمع TCP لنافذة الاتصال ([::]:port بوضع DualMode افتراضيًا). حد 4 اتصالات غير مصادقة معلّقة؛ أي اتصال زائد يُغلق فورًا
/// (docs/protocol.md القسم 2). "معلّق" = المعالج لم يُكمل بعد؛ المعالج يملك المقبس ويغلقه، ومهلتا TLS والمصادقة مسؤولية المستدعي.
/// </summary>
public sealed class TunnelListener : IAsyncDisposable
{
    public const int MaxPendingUnauthenticated = 4;

    private readonly Socket _socket;
    private int _pending;
    private int _inboundAttempts;
    private int _rejectedOverCapacity;
    private Func<Socket, CancellationToken, Task>? _handler;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private readonly List<Task> _handlerTasks = new();
    private readonly object _gate = new();

    /// <param name="port">0 = يعيّنه النظام.</param>
    /// <param name="bindAddress">افتراضيًا IPv6Any بوضع DualMode (مع سقوط إلى IPv4Any إن لم يتوفر IPv6).</param>
    public TunnelListener(int port = 0, IPAddress? bindAddress = null, int backlog = 16)
    {
        _socket = Bind(bindAddress, port, backlog);
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        Port = LocalEndPoint.Port;
    }

    public int Port { get; }
    public IPEndPoint LocalEndPoint { get; }
    public bool IsRunning => _acceptLoop is { IsCompleted: false };
    public int Pending => Volatile.Read(ref _pending);
    public int InboundAttempts => Volatile.Read(ref _inboundAttempts);
    public int RejectedOverCapacity => Volatile.Read(ref _rejectedOverCapacity);

    /// <summary>يبدأ حلقة القبول. المعالج يملك المقبس؛ عند اكتماله يُحرَّر مكان من الأربعة.</summary>
    public Task StartAsync(Func<Socket, CancellationToken, Task> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_acceptLoop is not null) throw new InvalidOperationException("listener already started");
            _handler = handler;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        return Task.CompletedTask;
    }

    /// <summary>يوقف القبول ويغلق مقبس الاستماع وينتظر انتهاء المعالجات الجارية (التي تُلغى عبر الرمز).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        Task[] handlers;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _acceptLoop;
            handlers = _handlerTasks.ToArray();
        }
        try { _socket.Close(); } catch { /* تجاهل */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* تجاهل */ }
        }
        try { await Task.WhenAll(handlers).ConfigureAwait(false); } catch { /* المعالجات تبتلع أخطاءها */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _socket.Dispose();
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            Interlocked.Increment(ref _inboundAttempts);
            if (Interlocked.Increment(ref _pending) > MaxPendingUnauthenticated)
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _rejectedOverCapacity);
                SafeClose(accepted);
                continue;
            }

            var task = RunHandlerAsync(accepted, ct);
            lock (_gate)
            {
                _handlerTasks.RemoveAll(t => t.IsCompleted);
                _handlerTasks.Add(task);
            }
        }
    }

    private async Task RunHandlerAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            socket.NoDelay = true;
            await _handler!(socket, ct).ConfigureAwait(false);
        }
        catch
        {
            // المعالج مسؤول عن تسجيل أخطائه وإغلاق مقبسه.
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private static void SafeClose(Socket socket)
    {
        try { socket.Close(0); } catch { /* تجاهل */ }
    }

    private static Socket Bind(IPAddress? bindAddress, int port, int backlog)
    {
        var address = bindAddress ?? IPAddress.IPv6Any;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            try
            {
                var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                v6.Bind(new IPEndPoint(address, port));
                v6.Listen(backlog);
                return v6;
            }
            catch (SocketException) when (bindAddress is null)
            {
                address = IPAddress.Any; // لا IPv6 على هذا الجهاز
            }
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(address, port));
        socket.Listen(backlog);
        return socket;
    }
}
