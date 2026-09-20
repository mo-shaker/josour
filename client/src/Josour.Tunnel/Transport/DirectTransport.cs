using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>The direct transport: a TCP connection with a timeout (a DualMode socket + NoDelay) returning a NetworkStream that owns the socket. RelayTransport is added later behind the same interface.</summary>
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

        // A DualMode socket connects to a v4 address through its mapped form; if IPv6 is unavailable on the machine we fall back to an ordinary v4 socket.
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
