using System.Net;

namespace RouteBridge.Core.Net;

/// <summary>نقطة نهاية Proxy النظام: مضيف ومنفذ فقط (لا مخطط ولا مسار).</summary>
public sealed record SystemProxyEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// من أين نعرف Proxy النظام. الوجهة تُمرَّر لأن قائمة التجاوز (bypass) قد تستثني بعض الوجهات،
/// وقد يختلف الـ Proxy بين http وhttps.
/// </summary>
public interface ISystemProxyResolver
{
    /// <summary>الـ Proxy لهذه الوجهة، أو null إن لم يُضبط شيء أو كانت الوجهة ضمن قائمة التجاوز.</summary>
    SystemProxyEndpoint? Resolve(Uri destination);

    /// <summary>هل ضُبط Proxy على هذا الجهاز أصلًا (لمفتاح <c>system_proxy_present</c> في التشخيص)؟</summary>
    bool IsConfigured { get; }
}

/// <summary>لا Proxy أبدًا. الافتراضي في الاختبارات كي لا تعتمد على إعدادات جهاز المطوّر.</summary>
public sealed class NoSystemProxy : ISystemProxyResolver
{
    public static NoSystemProxy Instance { get; } = new();
    public bool IsConfigured => false;
    public SystemProxyEndpoint? Resolve(Uri destination) => null;
}

/// <summary>Proxy ثابت لوجهة واحدة أو لكل الوجهات (للاختبارات ولإعداد يدوي لاحقًا).</summary>
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
/// Proxy النظام كما يراه .NET: <see cref="HttpClient.DefaultProxy"/>.
/// على Windows يقرأ إعدادات WinHTTP/WinINET (بما فيها قائمة التجاوز وملف PAC)؛ وعلى غيرها يقرأ
/// <c>http_proxy</c>/<c>https_proxy</c>/<c>all_proxy</c>/<c>no_proxy</c>. القرار كله (بما فيه bypass)
/// من <see cref="IWebProxy"/> نفسه، فلا نعيد تنفيذ منطق التجاوز.
///
/// <para><b>حراسة إضافية:</b> لا يُعاد Proxy لوجهة loopback أو لاسم محلي — تلك ترفضها سياسة التوجيه أصلًا،
/// والذهاب بها إلى Proxy يفتح مسارًا غير متوقع. أما أن يكون <b>الـ Proxy نفسه</b> على عنوان خاص أو loopback
/// (10.x أو 127.0.0.1:8080) فهو الحالة الطبيعية في الشركات ومسموح: مصدره إعداد الجهاز لا محتوى الطلب.</para>
/// </summary>
public sealed class SystemProxyResolver : ISystemProxyResolver
{
    private readonly Func<IWebProxy?> _proxySource;

    public SystemProxyResolver(Func<IWebProxy?>? proxySource = null)
        => _proxySource = proxySource ?? (() => HttpClient.DefaultProxy);

    /// <summary>النسخة المشتركة التي يستعملها الإنتاج.</summary>
    public static SystemProxyResolver Default { get; } = new();

    public bool IsConfigured
    {
        get
        {
            // وجهة تمثيلية: لو أعاد الـ Proxy شيئًا لها فهو مضبوط.
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
            // بعض التنفيذات تعيد الوجهة نفسها بمعنى «بلا Proxy».
            if (uri.Host.Equals(destination.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == destination.Port) return null;
            if (string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535) return null;
            // مخططات غير HTTP (socks) لا يتكلمها هذا المسار.
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return null;
            return new SystemProxyEndpoint(uri.Host, uri.Port);
        }
        catch
        {
            return null;
        }
    }
}
