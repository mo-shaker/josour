using System.Net.Sockets;

namespace Josour.Tunnel.Mux;

/// <summary>NetworkStream يملك مقبسه ويدعم الإغلاق النصفي بـ Shutdown(Send) (docs/protocol.md القسم 5: CLOSE = إغلاق نصفي).</summary>
public sealed class SocketStream : NetworkStream, IHalfClosable
{
    private int _sendShutdown;

    public SocketStream(Socket socket) : base(socket, ownsSocket: true)
    {
        try { socket.NoDelay = true; } catch (SocketException) { /* تجاهل */ }
    }

    public ValueTask CompleteWritingAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _sendShutdown, 1) == 0)
        {
            try { Socket.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { /* المقبس مغلق أصلًا */ }
            catch (ObjectDisposedException) { /* مغلق */ }
        }
        return ValueTask.CompletedTask;
    }
}
