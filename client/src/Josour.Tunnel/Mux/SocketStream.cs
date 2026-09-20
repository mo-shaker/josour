using System.Net.Sockets;

namespace Josour.Tunnel.Mux;

/// <summary>A NetworkStream that owns its socket and supports the half-close through Shutdown(Send) (docs/protocol.md section 5: CLOSE = a half-close).</summary>
public sealed class SocketStream : NetworkStream, IHalfClosable
{
    private int _sendShutdown;

    public SocketStream(Socket socket) : base(socket, ownsSocket: true)
    {
        try { socket.NoDelay = true; } catch (SocketException) { /* ignore */ }
    }

    public ValueTask CompleteWritingAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _sendShutdown, 1) == 0)
        {
            try { Socket.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { /* the socket is already closed */ }
            catch (ObjectDisposedException) { /* closed */ }
        }
        return ValueTask.CompletedTask;
    }
}
