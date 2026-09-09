using System.Net;
using System.Net.Sockets;
using Josour.Core.Net;

namespace Josour.Egress.Tests;

/// <summary>محلل أسماء ثابت للاختبارات: اسم → عناوين، أو استثناء.</summary>
internal sealed class StubResolver : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.Ordinal);
    public int Calls { get; private set; }
    public List<string> Requested { get; } = new();

    public StubResolver Map(string host, params string[] addresses)
    {
        _map[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        Requested.Add(host);
        return _map.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>مستمع TCP محلي يمثل الموقع الوجهة.</summary>
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

    public Task<Socket> AcceptAsync(CancellationToken ct = default) => _listener.AcceptSocketAsync(ct).AsTask();

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

internal static class Policies
{
    /// <summary>يسمح بـ loopback فقط داخل الاختبارات (سياسة النطاقات الحقيقية تحظره).</summary>
    public static readonly Func<IPAddress, bool> AllowLoopbackOnly = a => !IPAddress.IsLoopback(a) && IpRangePolicy.IsBlocked(a);
}
