using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Tests;

internal static class Loopback
{
    /// <summary>A pair of NetworkStreams connected over 127.0.0.1. Each stream owns its socket.</summary>
    public static async Task<(Stream A, Stream B)> CreatePairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptSocketAsync();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);
            var server = await acceptTask;
            return (new NetworkStream(client, ownsSocket: true), new NetworkStream(server, ownsSocket: true));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A port nobody is listening on (opened and then closed at once).</summary>
    public static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal static class TestMaterial
{
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

    public static SessionMaterial Create(TunnelRole role, Guid? sessionId = null, byte[]? secret = null, bool samePublicIp = true)
        => new(sessionId ?? Guid.NewGuid(), role, secret ?? NewSecret(), DateTimeOffset.UtcNow.AddMinutes(30), samePublicIp, "203.0.113.1");
}

internal static class StreamAssert
{
    /// <summary>It asserts that no byte arrives within the timeout (the read ends on cancellation rather than on data).</summary>
    public static async Task NothingReadableAsync(Stream stream, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);
        var buffer = new byte[1];
        try
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            Assert.Fail(n == 0 ? "stream was closed" : "unexpected byte received");
        }
        catch (OperationCanceledException)
        {
            // Expected: no data
        }
    }

    public static async Task<bool> IsClosedWithinAsync(Stream stream, TimeSpan within)
    {
        using var cts = new CancellationTokenSource(within);
        var buffer = new byte[1];
        try
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            return n == 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }
    }
}
