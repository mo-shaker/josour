using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Core.Browser;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

internal sealed class FakeMux : IMuxConnection
{
    public List<(string Host, int Port)> Opens { get; } = new();
    public Func<string, int, CancellationToken, Task<MuxOpenResult>> OnOpen { get; set; } = (_, _, _) => Task.FromResult(MuxOpenResult.Fail(OpenFailReason.NotAllowed));
    public bool Closed { get; set; }

    public Task<MuxOpenResult> OpenStreamAsync(string host, int port, CancellationToken ct)
    {
        if (Closed) throw new MuxClosedException("closed");
        lock (Opens) Opens.Add((host, port));
        return OnOpen(host, port, ct);
    }

    public MuxStats Stats => default;
    public Task Completion => Task.CompletedTask;
    public GoAwayReason? RemoteGoAway => null;
    public Task<TimeSpan> PingAsync(CancellationToken ct) => Task.FromResult(TimeSpan.Zero);
    public Task CloseAsync(GoAwayReason reason) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeBrowser : IBrowserSession
{
    public HashSet<int> Owned { get; } = new();
    public bool IsRunning => true;
    public bool OwnsProcess(int pid) => Owned.Contains(pid);
    public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct) => Task.FromResult(new BrowserLaunchResult(true, null, null));
    public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakePidChecker : IOwnerPidChecker
{
    public int? Pid { get; set; }
    public int Calls { get; private set; }
    public int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
    {
        Calls++;
        return Pid;
    }
}

internal sealed class StubResolver : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.Ordinal);
    public int Calls { get; private set; }

    public StubResolver Map(string host, params string[] addresses)
    {
        _map[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        return _map.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>A local TCP listener standing in for a destination site.</summary>
internal sealed class LocalOrigin : IDisposable
{
    private readonly TcpListener _listener;

    public LocalOrigin()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }
    public Task<Socket> AcceptAsync() => _listener.AcceptSocketAsync();

    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>A primitive HTTP origin: it accepts one connection, reads the request head, answers with a fixed body and closes.</summary>
internal sealed class HttpOrigin : IDisposable
{
    private readonly LocalOrigin _origin = new();

    public int Port => _origin.Port;
    public string? ReceivedHead { get; private set; }

    public Task ServeOnceAsync(string body) => Task.Run(async () =>
    {
        using var socket = await _origin.AcceptAsync();
        using var stream = new NetworkStream(socket, true);
        var request = await HttpRequestParser.ReadAsync(stream, CancellationToken.None);
        ReceivedHead = request is null ? null : $"{request.Method} {request.Target} {request.Version}\r\n" + string.Join("\r\n", request.Headers.Select(h => $"{h.Key}: {h.Value}"));
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(payload);
        socket.Shutdown(SocketShutdown.Send);
        // We wait for the other side to close, then close
        var buffer = new byte[1024];
        try { while (await stream.ReadAsync(buffer) > 0) { } } catch { }
    });

    public void Dispose() => _origin.Dispose();
}

internal sealed record ResponseHead(int Status, Dictionary<string, string> Headers, byte[] Remainder);

/// <summary>A raw client on a socket towards the proxy.</summary>
internal sealed class ProxyClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private ProxyClient(Socket socket)
    {
        Socket = socket;
        Stream = new NetworkStream(socket, true);
    }

    public Socket Socket { get; }
    public NetworkStream Stream { get; }

    public static async Task<ProxyClient> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
        return new ProxyClient(socket);
    }

    public async Task SendAsync(string text)
    {
        await Stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask().WaitAsync(Timeout);
        await Stream.FlushAsync();
    }

    public Task<ResponseHead> ReadHeadAsync() => ReadHeadAsync(Stream);

    public static async Task<ResponseHead> ReadHeadAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var filled = 0;
        while (true)
        {
            var idx = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (idx >= 0)
            {
                var text = Encoding.ASCII.GetString(buffer, 0, idx);
                var lines = text.Split("\r\n");
                var status = int.Parse(lines[0].Split(' ')[1]);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                return new ResponseHead(status, headers, buffer.AsSpan(idx + 4, filled - idx - 4).ToArray());
            }
            var n = await stream.ReadAsync(buffer.AsMemory(filled)).AsTask().WaitAsync(Timeout);
            if (n == 0) throw new EndOfStreamException($"closed after {filled} bytes: {Encoding.ASCII.GetString(buffer, 0, filled)}");
            filled += n;
        }
    }

    public async Task<byte[]> ReadBodyAsync(ResponseHead head)
    {
        var length = int.Parse(head.Headers["Content-Length"]);
        var body = new byte[length];
        head.Remainder.AsSpan(0, Math.Min(length, head.Remainder.Length)).CopyTo(body);
        var have = Math.Min(length, head.Remainder.Length);
        while (have < length)
        {
            var n = await Stream.ReadAsync(body.AsMemory(have)).AsTask().WaitAsync(Timeout);
            if (n == 0) throw new EndOfStreamException();
            have += n;
        }
        return body;
    }

    /// <summary>Was the connection closed with no bytes at all?</summary>
    public async Task<bool> ClosedWithoutDataAsync()
    {
        var one = new byte[1];
        try
        {
            var n = await Stream.ReadAsync(one).AsTask().WaitAsync(Timeout);
            return n == 0;
        }
        // An immediate close may arrive as a clean FIN or as an RST; both are "closed with no data" for the purpose of this check.
        catch (IOException) { return true; }
        catch (SocketException) { return true; }
    }

    public async Task<byte[]> ReadToEndAsync()
    {
        var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await Stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            if (n == 0) return ms.ToArray();
            ms.Write(buffer, 0, n);
        }
    }

    public ValueTask DisposeAsync()
    {
        Stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class TcpPair
{
    /// <summary>A pair of sockets on loopback: a SocketStream (as if it were the tunnel's stream) and the far socket for the test.</summary>
    public static async Task<(SocketStream Near, Socket Far)> CreateAsync()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, origin.Port);
        return (new SocketStream(client), await accept);
    }
}

internal static class Proxies
{
    /// <summary>Waits for a condition satisfied by a background task (the refusal and the counting happen after the socket is closed).</summary>
    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    public static readonly string[] DefaultEntries = { "allowed.example", "=exact.example", "portal.example:8443" };

    /// <summary>
    /// Blocking addresses as in production except loopback: the origins in these tests live on 127.0.0.1, and the real policy
    /// (exercised in <c>ConnectProxyServerBlockedAddressTests</c>) refuses them. The counterpart of <c>InProcessTunnelPair</c> on the host.
    /// </summary>
    public static bool BlockedExceptLoopback(IPAddress address)
        => !IPAddress.IsLoopback(address) && IpRangePolicy.IsBlocked(address);

    /// <summary>The production address policy with no exemption at all (the tests for blocking after resolution).</summary>
    public static readonly Func<IPAddress, bool> RealAddressPolicy = address => IpRangePolicy.IsBlocked(address);

    public static ConnectProxyServer Start(
        FakeMux? mux = null,
        StubResolver? resolver = null,
        IBrowserSession? browser = null,
        IOwnerPidChecker? checker = null,
        bool? rejectUnknown = null,
        string[]? entries = null,
        int[]? allowedPorts = null,
        Func<IPAddress, bool>? addressBlocker = null,
        ISystemProxyResolver? systemProxy = null)
    {
        var server = new ConnectProxyServer(new ConnectProxyOptions
        {
            Allowlist = AllowlistMatcher.Parse(1, entries ?? DefaultEntries),
            AllowedPorts = allowedPorts ?? new[] { 80, 443 },
            PeerPublicIp = "203.0.113.9",
            Mux = mux,
            Resolver = resolver ?? new StubResolver(),
            Browser = browser,
            OwnerPidChecker = checker ?? PermissiveOwnerPidChecker.Instance,
            RejectUnknownOwner = rejectUnknown,
            AddressBlocker = addressBlocker ?? BlockedExceptLoopback,
            // The default: no system proxy. The tests do not depend on the developer machine's settings or its environment variables.
            SystemProxy = systemProxy ?? NoSystemProxy.Instance,
            DirectConnectTimeout = TimeSpan.FromSeconds(3),
        });
        server.Start();
        return server;
    }
}
