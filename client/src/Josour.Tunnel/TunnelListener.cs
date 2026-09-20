using System.Net;
using System.Net.Sockets;

namespace Josour.Tunnel;

/// <summary>
/// The TCP listener for the connect window ([::]:port in DualMode by default). A limit of 4 pending unauthenticated connections; anything beyond that is closed at once
/// (docs/protocol.md section 2). "Pending" = the handler has not finished; the handler owns the socket and closes it, and the TLS and authentication timeouts are the caller's responsibility.
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

    /// <param name="port">0 = the system assigns it.</param>
    /// <param name="bindAddress">IPv6Any in DualMode by default (falling back to IPv4Any if IPv6 is unavailable).</param>
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

    /// <summary>
    /// The connections closed before passing AUTH1. The listener records here what it refuses itself (exceeding the four pending); the authentication's
    /// own result is recorded by <see cref="SymmetricConnector"/>, because it alone knows it. It stays readable after
    /// <see cref="StopAsync"/> and <see cref="DisposeAsync"/> so it can be reported in <c>POST /api/v1/diagnostics</c>.
    /// </summary>
    public UnauthenticatedProbeLog Probes { get; } = new();

    /// <summary>Starts the accept loop. The handler owns the socket; when it completes, one of the four slots is freed.</summary>
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

    /// <summary>Stops accepting, closes the listening socket, and waits for the handlers under way (which are cancelled through the token).</summary>
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
        try { _socket.Close(); } catch { /* ignore */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        try { await Task.WhenAll(handlers).ConfigureAwait(false); } catch { /* the handlers swallow their own errors */ }
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
                // Closed with not one byte read, so it is by definition a connection that did not pass AUTH1.
                Probes.Record(RemoteAddress(accepted));
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
            // The handler is responsible for logging its errors and closing its socket.
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private static void SafeClose(Socket socket)
    {
        try { socket.Close(0); } catch { /* ignore */ }
    }

    /// <summary>The other side's address if the socket still knows it (it may have closed between us and the accept).</summary>
    private static IPAddress? RemoteAddress(Socket socket)
    {
        try { return (socket.RemoteEndPoint as IPEndPoint)?.Address; }
        catch (ObjectDisposedException) { return null; }
        catch (SocketException) { return null; }
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
                address = IPAddress.Any; // no IPv6 on this machine
            }
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(address, port));
        socket.Listen(backlog);
        return socket;
    }
}
