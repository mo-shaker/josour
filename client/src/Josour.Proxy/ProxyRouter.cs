using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy;

public enum RouteKind { Reject, Tunnel, Direct }

/// <summary>Host is normalised for Tunnel/Direct; Reason is descriptive for Reject.</summary>
public sealed record RouteDecision(RouteKind Kind, string Host, int Port, string? Reason);

/// <summary>
/// The routing decision on the user's side (docs/protocol.md and plan 7.5): an address literal (any address) or a local name -> a local 403 refusal (defence in depth;
/// an IP cannot be listed to begin with); allowed -> through the tunnel; otherwise -> a direct connection from the user's machine (ADR-0004)
/// when <c>directAllowed</c>, and a 403 when it is not.
/// <para>
/// Direct is only ever a choice in the host's enforce mode, where "not on the list" means "use your own connection". With every site
/// allowed the system proxy sends the whole machine here, and anything that is not tunnelled — a port outside <c>allowed_ports</c>, an
/// OPEN_FAIL — must be refused rather than leave quietly from the guest's own address (ADR-0015 point 3).
/// </para>
/// </summary>
public static class ProxyRouter
{
    public static RouteDecision Decide(string host, int port, IAllowlist allowlist, IReadOnlyList<int> allowedPorts, bool directAllowed = true)
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
        // Normalisation can turn a name into a literal: "8.8.8.8." (the trailing dot), fullwidth digits, "0x7f.0.0.1.".
        if (IpRangePolicy.IsIpLiteral(normalized)) return new RouteDecision(RouteKind.Reject, normalized, port, "ip_literal");
        if (IsLocalName(normalized)) return new RouteDecision(RouteKind.Reject, normalized, port, "local");

        if (allowlist.IsAllowed(normalized, port, allowedPorts)) return new RouteDecision(RouteKind.Tunnel, normalized, port, null);
        if (directAllowed) return new RouteDecision(RouteKind.Direct, normalized, port, null);
        return new RouteDecision(RouteKind.Reject, normalized, port, allowedPorts.Contains(port) ? "not_allowed" : "port_not_allowed");
    }

    /// <summary>
    /// Is there a direct path at all? Only when the host enforces a list (an <see cref="AllowlistMatcher"/> loaded from the server):
    /// with <see cref="AllowAllAllowlist"/> everything the host allows is tunnelled and nothing else may leave from the guest's own
    /// connection. The test is positive on purpose, so any other list — <see cref="DenyAllAllowlist"/> (no list supplied) or one this
    /// code does not know — fails closed.
    /// </summary>
    public static bool DirectAllowedFor(IAllowlist allowlist) => allowlist is AllowlistMatcher;

    /// <summary>localhost and *.localhost are refused locally (the browser does not usually pass them to the proxy, but defence in depth is required).</summary>
    public static bool IsLocalName(string normalizedHost)
        => normalizedHost == "localhost" || normalizedHost.EndsWith(".localhost", StringComparison.Ordinal);

    /// <summary>OPEN_FAIL -> a truthful HTTP code for the browser: 403 policy, 502 unreachable, 503 limits.</summary>
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
        400 => "Bad Request",
        403 => "Forbidden",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error",
    };
}
