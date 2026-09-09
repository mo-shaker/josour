using System.Net;

namespace Josour.Core.Net;

/// <summary>حل الأسماء (docs/protocol.md القسم 6 الخطوة 5). قابل للحقن لاختبار سياسة الخروج والـ Proxy بلا DNS حقيقي.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Dns.GetHostAddressesAsync كما هو.</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public static readonly DnsHostResolver Instance = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Dns.GetHostAddressesAsync(host, ct);
    }
}
