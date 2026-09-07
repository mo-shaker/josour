using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy;

public enum RouteKind { Reject, Tunnel, Direct }

/// <summary>Host مطبَّع عند Tunnel/Direct؛ Reason وصفي عند Reject.</summary>
public sealed record RouteDecision(RouteKind Kind, string Host, int Port, string? Reason);

/// <summary>
/// قرار التوجيه على جانب المستخدم (docs/protocol.md وخطة 7.5): IP حرفي (أي عنوان) أو اسم محلي → رفض محلي 403 (دفاع في العمق؛
/// لا يمكن إدراج IP في القائمة أصلًا)؛ مسموح → عبر النفق؛ غيره → اتصال مباشر من جهاز المستخدم (ADR-0004).
/// </summary>
public static class ProxyRouter
{
    public static RouteDecision Decide(string host, int port, IAllowlist allowlist, IReadOnlyList<int> allowedPorts)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(allowedPorts);
        if (port is < 1 or > 65535) return new RouteDecision(RouteKind.Reject, host, port, "bad_port");
        var raw = (host ?? string.Empty).Trim();
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']') raw = raw[1..^1];
        if (raw.Length == 0) return new RouteDecision(RouteKind.Reject, raw, port, "empty_host");
        if (IpRangePolicy.IsIpLiteral(raw)) return new RouteDecision(RouteKind.Reject, raw, port, "ip_literal");

        string normalized;
        try { normalized = AllowlistMatcher.NormalizeHost(raw); }
        catch (ArgumentException) { return new RouteDecision(RouteKind.Reject, raw, port, "bad_host"); }
        if (IsLocalName(normalized)) return new RouteDecision(RouteKind.Reject, normalized, port, "local");

        return allowlist.IsAllowed(normalized, port, allowedPorts)
            ? new RouteDecision(RouteKind.Tunnel, normalized, port, null)
            : new RouteDecision(RouteKind.Direct, normalized, port, null);
    }

    /// <summary>localhost و*.localhost تُرفض محليًا (المتصفح لا يمررها للـ Proxy عادةً، لكن الدفاع في العمق مطلوب).</summary>
    public static bool IsLocalName(string normalizedHost)
        => normalizedHost == "localhost" || normalizedHost.EndsWith(".localhost", StringComparison.Ordinal);

    /// <summary>هل الاسم مطابق لأي مدخل في القائمة بغض النظر عن المنفذ (لرد 307 على http://).</summary>
    public static bool HostIsAllowlisted(string normalizedHost, IAllowlist allowlist)
        => allowlist.Entries.Any(entry => AllowlistMatcher.HostMatches(normalizedHost, entry));

    /// <summary>OPEN_FAIL → رمز HTTP صادق للمتصفح: 403 سياسة، 502 فشل وصول، 503 حدود.</summary>
    public static int StatusForOpenFail(OpenFailReason reason) => reason switch
    {
        OpenFailReason.NotAllowed or OpenFailReason.PrivateIp or OpenFailReason.PortNotAllowed or OpenFailReason.IpLiteral => 403,
        OpenFailReason.DnsFailed or OpenFailReason.ConnectFailed => 502,
        OpenFailReason.Limit => 503,
        _ => 502,
    };

    public static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        307 => "Temporary Redirect",
        400 => "Bad Request",
        403 => "Forbidden",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error",
    };
}
