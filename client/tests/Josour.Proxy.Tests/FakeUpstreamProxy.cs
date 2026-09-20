using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Net;

namespace Josour.Proxy.Tests;

/// <summary>
/// (Shared: linked into <c>Josour.E2E.Tests</c> as well; one behaviour for both.)
/// A local upstream proxy standing in for a company network's proxy: it accepts <c>CONNECT host:port</c> and connects to the real origin,
/// and <c>GET http://host/path</c> (an absolute-URI) which it answers itself. It can demand authentication (407) once.
/// </summary>
internal sealed class FakeUpstreamProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _requestLines = new();
    private readonly List<string?> _authorizations = new();
    private readonly object _gate = new();

    public FakeUpstreamProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>It answers 407 to every request with no <c>Proxy-Authorization</c>.</summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>It answers 502 to every CONNECT (to test the truthful failure).</summary>
    public bool RefuseConnect { get; set; }

    /// <summary>The reply body for ordinary http requests.</summary>
    public string HttpBody { get; set; } = "from upstream proxy";

    /// <summary>It sends these bytes with the CONNECT reply head itself (to test the bytes that follow the head).</summary>
    public byte[] ConnectRemainder { get; set; } = Array.Empty<byte>();

    public IReadOnlyList<string> RequestLines { get { lock (_gate) return _requestLines.ToArray(); } }
    public IReadOnlyList<string?> Authorizations { get { lock (_gate) return _authorizations.ToArray(); } }
    public int Connections { get; private set; }

    public SystemProxyEndpoint Endpoint => new("127.0.0.1", Port);

    public ISystemProxyResolver AsResolver(Func<Uri, bool>? bypass = null)
        => new StaticSystemProxy(Endpoint, bypass);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_cts.Token); }
            catch (Exception) { return; }
            Connections++;
            _ = Task.Run(() => HandleAsync(socket));
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var request = await HttpRequestParser.ReadAsync(stream, _cts.Token);
            if (request is null) return;
            var authorization = request.Header("Proxy-Authorization");
            lock (_gate)
            {
                _requestLines.Add($"{request.Method} {request.Target} {request.Version}");
                _authorizations.Add(authorization);
            }

            if (RequireAuthentication && string.IsNullOrEmpty(authorization))
            {
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            if (request.IsConnect)
            {
                if (RefuseConnect)
                {
                    await WriteAsync(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                await ConnectAsync(stream, request);
                return;
            }

            var payload = Encoding.UTF8.GetBytes(HttpBody);
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(payload, _cts.Token);
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // One connection failed
        }
    }

    private async Task ConnectAsync(NetworkStream client, ProxyRequest request)
    {
        if (!HttpRequestParser.TryParseAuthority(request.Target, 443, out var host, out var port)) return;
        using var origin = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await origin.ConnectAsync(IPAddress.Loopback, port, _cts.Token); }
        catch (Exception)
        {
            await WriteAsync(client, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return;
        }
        _ = host;

        await WriteAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n");
        if (ConnectRemainder.Length > 0) await client.WriteAsync(ConnectRemainder, _cts.Token);
        using var far = new NetworkStream(origin, ownsSocket: false);
        var up = Copy(client, far);
        var down = Copy(far, client);
        await Task.WhenAny(up, down);
    }

    private async Task Copy(Stream from, Stream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, _cts.Token);
                if (n == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                await to.FlushAsync(_cts.Token);
            }
        }
        catch (Exception) { /* closed */ }
    }

    private Task WriteAsync(Stream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token).AsTask();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { /* ignore */ }
        _cts.Dispose();
    }
}
