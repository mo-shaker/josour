using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>
/// نقل يقبل <b>اسم مضيف</b> في <see cref="CandidateEndpoint.Ip"/>، يحلّه ثم يفوّض الاتصال إلى نقل داخلي.
///
/// <para>
/// <see cref="DirectTransport"/> يرفض أي شيء غير عنوان IP حرفي، وهذا ليس تشددًا زائدًا بل ضابط أمني:
/// المرشحون تصل من <c>session.peer_endpoint</c>، أي من الطرف الآخر عبر الخادم، والعقد يشترط أن تكون عناوين
/// حرفية (<c>docs/ws-protocol.md</c> القسم 5). لو قَبِل أسماء لصار بإمكان النظير أن يجعلنا نحلّ ما يختاره.
/// </para>
/// <para>
/// عنوان الـ Relay حالة أخرى تمامًا: يأتي في <c>session.created.relay</c> من <b>خادمنا نحن</b>، وهو اسم بحكم
/// التصميم (<c>RELAY_HOST</c> في ADR-0009). فالفرق بين المسارين فرق مصدر لا فرق تشدد، ولذلك حُلَّ بنقل ثانٍ
/// بدل توسيع الأول.
/// </para>
/// </summary>
public sealed class ResolvingTransport : ITunnelTransport
{
    private readonly ITunnelTransport _inner;

    public ResolvingTransport(ITunnelTransport? inner = null) => _inner = inner ?? new DirectTransport();

    public string Name => _inner.Name;

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (IPAddress.TryParse(endpoint.Ip, out _))
            return await _inner.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.Ip, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        // اسم واحد قد يحل إلى IPv6 وIPv4 معًا، وقد لا يكون أولهما قابلًا للوصول من هذه الشبكة. نجرّبها
        // بالترتيب داخل المهلة الكلية بدل أن نتوقف عند أول إخفاق.
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        foreach (var address in addresses)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                return await _inner
                    .ConnectAsync(endpoint with { Ip = address.ToString() }, remaining, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or TimeoutException && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
