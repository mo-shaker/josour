using System.Net;

namespace Josour.Core.Net;

/// <summary>The system proxy's endpoint: a host and a port only (no scheme and no path).</summary>
public sealed record SystemProxyEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// Where we learn the system proxy from. The destination is passed because the bypass list may exempt some destinations,
/// and the proxy may differ between http and https.
/// </summary>
public interface ISystemProxyResolver
{
    /// <summary>The proxy for this destination, or null if nothing is configured or the destination is in the bypass list.</summary>
    SystemProxyEndpoint? Resolve(Uri destination);

    /// <summary>Is a proxy configured on this machine at all (for the <c>system_proxy_present</c> key in the diagnostics)?</summary>
    bool IsConfigured { get; }
}

/// <summary>Never a proxy. The default in the tests, so they do not depend on the developer machine's settings.</summary>
public sealed class NoSystemProxy : ISystemProxyResolver
{
    public static NoSystemProxy Instance { get; } = new();
    public bool IsConfigured => false;
    public SystemProxyEndpoint? Resolve(Uri destination) => null;
}

/// <summary>A fixed proxy for one destination or for all of them (for the tests, and for manual configuration later).</summary>
public sealed class StaticSystemProxy : ISystemProxyResolver
{
    private readonly SystemProxyEndpoint _endpoint;
    private readonly Func<Uri, bool>? _bypass;

    public StaticSystemProxy(SystemProxyEndpoint endpoint, Func<Uri, bool>? bypass = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _bypass = bypass;
    }

    public bool IsConfigured => true;

    public SystemProxyEndpoint? Resolve(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return _bypass is not null && _bypass(destination) ? null : _endpoint;
    }
}

/// <summary>
/// The system proxy as .NET sees it: <see cref="HttpClient.DefaultProxy"/>.
/// On Windows it reads the WinHTTP/WinINET settings (including the bypass list and the PAC file); elsewhere it reads
/// <c>http_proxy</c>/<c>https_proxy</c>/<c>all_proxy</c>/<c>no_proxy</c>. The whole decision (bypass included) comes
/// from <see cref="IWebProxy"/> itself, so we do not reimplement the bypass logic.
///
/// <para><b>An extra guard:</b> no proxy is returned for a loopback destination or a local name — those are refused by the routing
/// policy to begin with, and taking them to a proxy opens an unexpected path. That <b>the proxy itself</b> is on a private or loopback
/// address (10.x or 127.0.0.1:8080) is the normal case in companies and is allowed: its source is the machine's configuration, not the request's content.</para>
/// </summary>
public sealed class SystemProxyResolver : ISystemProxyResolver
{
    private readonly Func<IWebProxy?> _proxySource;

    public SystemProxyResolver(Func<IWebProxy?>? proxySource = null)
        => _proxySource = proxySource ?? (() => HttpClient.DefaultProxy);

    /// <summary>The shared instance production uses.</summary>
    public static SystemProxyResolver Default { get; } = new();

    public bool IsConfigured
    {
        get
        {
            // A representative destination: if the proxy returns something for it, it is configured.
            try { return Resolve(new Uri("https://example.com/")) is not null; }
            catch { return false; }
        }
    }

    public SystemProxyEndpoint? Resolve(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.IsLoopback) return null;

        IWebProxy? proxy;
        try { proxy = _proxySource(); }
        catch { return null; }
        if (proxy is null) return null;

        try
        {
            if (proxy.IsBypassed(destination)) return null;
            var uri = proxy.GetProxy(destination);
            if (uri is null) return null;
            // Some implementations return the destination itself, meaning "no proxy".
            if (uri.Host.Equals(destination.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == destination.Port) return null;
            if (string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535) return null;
            // Non-HTTP schemes (socks) are not spoken on this path.
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return null;
            return new SystemProxyEndpoint(uri.Host, uri.Port);
        }
        catch
        {
            return null;
        }
    }
}
