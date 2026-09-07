using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>النقل المباشر: اتصال TCP بمهلة (مقبس DualMode + NoDelay) يعيد NetworkStream يملك المقبس. RelayTransport يُضاف لاحقًا خلف الواجهة نفسها.</summary>
public sealed class DirectTransport : ITunnelTransport
{
    public string Name => "direct";

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.TryParse(endpoint.Ip, out var address))
            throw new ArgumentException($"candidate ip is not an IP address", nameof(endpoint));
        if (endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("candidate port out of range", nameof(endpoint));

        var socket = CreateSocket(address, out var target);
        try
        {
            socket.NoDelay = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, endpoint.Port), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"TCP connect timed out after {timeout.TotalMilliseconds:F0} ms");
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Socket CreateSocket(IPAddress address, out IPAddress target)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            target = address;
            return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        }

        // مقبس DualMode يتصل بعنوان v4 عبر صيغته المغلَّفة؛ إن لم يتوفر IPv6 على الجهاز نسقط إلى مقبس v4 عادي.
        try
        {
            var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            target = address.MapToIPv6();
            return dual;
        }
        catch (SocketException)
        {
            target = address;
            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }
}
