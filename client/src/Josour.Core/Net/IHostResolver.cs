using System.Net;

namespace Josour.Core.Net;

/// <summary>Name resolution (docs/protocol.md section 6 step 5). Injectable so the egress policy and the proxy can be tested with no real DNS.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Dns.GetHostAddressesAsync as it is.</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public static readonly DnsHostResolver Instance = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Dns.GetHostAddressesAsync(host, ct);
    }
}
