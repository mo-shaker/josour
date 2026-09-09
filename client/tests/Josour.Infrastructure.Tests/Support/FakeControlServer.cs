using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Josour.Core.Control;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>
/// A self-contained in-process server that speaks docs/ws-protocol.md over a real WebSocket: a <see cref="TcpListener"/> on
/// 127.0.0.1, the RFC 6455 handshake by hand and <see cref="WebSocket.CreateFromStream"/> for the framing. No ASP.NET Core,
/// no <c>HttpListener</c> (which cannot upgrade on macOS), so the tests behave the same on every platform.
/// <para>
/// By default every connection answers <c>hello</c> with <see cref="Ack"/> and a client <c>ping</c> with <c>pong</c>;
/// tests override <see cref="OnHello"/>, close with a specific code, or drop the TCP connection outright.
/// </para>
/// </summary>
public sealed class FakeControlServer : IAsyncDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<FakeControlConnection> _connections = Channel.CreateUnbounded<FakeControlConnection>();
    private readonly List<FakeControlConnection> _all = new();
    private readonly object _gate = new();
    private readonly Task _acceptLoop;

    public FakeControlServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>What goes into <c>AppSettings.ServerUrl</c>; the channel turns it into <c>ws://127.0.0.1:port/ws</c>.</summary>
    public string ServerUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>The <c>hello.ack</c> the default hook sends.</summary>
    public HelloAckMessage Ack { get; set; } = new(
        DateTimeOffset.UtcNow,
        "203.0.113.7",
        new ServerSettings(120, 60, new[] { 80, 443 }, LogDomains: false),
        AllowlistVersion: 1);

    /// <summary>Replaces the default "answer hello with <see cref="Ack"/>" behaviour.</summary>
    public Func<FakeControlConnection, HelloMessage, Task>? OnHello { get; set; }

    /// <summary>Answer a client <c>ping</c> with <c>pong</c> (a real server always does; switch it off to test liveness).</summary>
    public bool AutoPong { get; set; } = true;

    /// <summary>Request paths seen by the handshake, in order (asserted to be <c>/ws</c>).</summary>
    public IReadOnlyList<string> RequestPaths
    {
        get
        {
            lock (_gate)
            {
                return _paths.ToList();
            }
        }
    }

    private readonly List<string> _paths = new();

    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>
    /// Every connection accepted so far, in order. A reconnect with no backoff can produce more than one, so a test that
    /// wants to talk to "the client" after a drop addresses all of them rather than guessing which one survived.
    /// </summary>
    public IReadOnlyList<FakeControlConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return _all.ToList();
            }
        }
    }

    /// <summary>Waits for the next client connection (already past the handshake).</summary>
    public async Task<FakeControlConnection> NextConnectionAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            return await _connections.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No WebSocket connection arrived within {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds:0.#} s.");
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandshakeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // the server was disposed
        }
    }

    private async Task HandshakeAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cts.Token);
            var lines = request.Split("\r\n");
            var path = lines[0].Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
            var key = lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim();

            if (key is null)
            {
                client.Dispose();
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            var response = "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
            await stream.FlushAsync(_cts.Token);

            var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            var connection = new FakeControlConnection(this, client, socket);
            lock (_gate)
            {
                _paths.Add(path);
                _all.Add(connection);
            }

            _connections.Writer.TryWrite(connection);
            await connection.PumpAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            client.Dispose();
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    internal Task HelloAsync(FakeControlConnection connection, HelloMessage hello) =>
        OnHello is null ? connection.SendAsync(Ack) : OnHello(connection, hello);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        List<FakeControlConnection> connections;
        lock (_gate)
        {
            connections = _all.ToList();
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // shutting down
        }

        _cts.Dispose();
    }
}

/// <summary>One accepted client connection: what it sent, and the frames the test makes the server send back.</summary>
public sealed class FakeControlConnection : IAsyncDisposable
{
    private readonly FakeControlServer _server;
    private readonly TcpClient _client;
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<ControlMessage> _received = Channel.CreateUnbounded<ControlMessage>();
    private readonly List<ControlMessage> _all = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FakeControlConnection(FakeControlServer server, TcpClient client, WebSocket socket)
    {
        _server = server;
        _client = client;
        _socket = socket;
    }

    /// <summary>Everything the client sent, in order.</summary>
    public IReadOnlyList<ControlMessage> Received
    {
        get
        {
            lock (_gate)
            {
                return _all.ToList();
            }
        }
    }

    /// <summary>The close status the client sent, once it closed its side.</summary>
    public WebSocketCloseStatus? ClientCloseStatus { get; private set; }

    /// <summary>Completes when this connection's pump ends (client closed, dropped or the server disposed).</summary>
    public Task Finished => _finished.Task;

    public async Task<T> NextAsync<T>(Func<T, bool>? where = null, TimeSpan? timeout = null)
        where T : ControlMessage
    {
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                var message = await _received.Reader.ReadAsync(cts.Token);
                if (message is T typed && (where is null || where(typed)))
                {
                    return typed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"The client did not send a {typeof(T).Name} within {wait.TotalSeconds:0.#} s. Seen: {string.Join(", ", Received.Select(m => m.Type))}");
        }
    }

    public Task SendAsync(ControlMessage message) => SendRawAsync(ControlMessageSerializer.Serialize(message));

    public async Task SendRawAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Closes with one of the contract's codes (4401/4403/4409/1012) or any other status.</summary>
    public async Task CloseAsync(int closeCode, string description)
    {
        await _sendGate.WaitAsync();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, description, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // the client is already gone
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Cuts the TCP connection without a close frame (server crash / network drop).</summary>
    public void Drop()
    {
        try
        {
            _socket.Abort();
        }
        catch (Exception)
        {
            // already gone
        }

        _client.Close();
    }

    internal async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ClientCloseStatus = result.CloseStatus;
                    try
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        // the client left without waiting
                    }

                    break;
                }

                var count = result.Count;
                while (!result.EndOfMessage)
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
                    count += result.Count;
                }

                var message = ControlMessageSerializer.Deserialize(buffer.AsSpan(0, count));
                lock (_gate)
                {
                    _all.Add(message);
                }

                _received.Writer.TryWrite(message);

                switch (message)
                {
                    case HelloMessage hello:
                        await _server.HelloAsync(this, hello);
                        break;
                    case PingMessage when _server.AutoPong:
                        await SendAsync(new PongMessage());
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
            // the connection ended
        }
        finally
        {
            _finished.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Drop();
        try
        {
            await Finished.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // shutting down
        }

        _socket.Dispose();
        _client.Dispose();
        _sendGate.Dispose();
    }
}
