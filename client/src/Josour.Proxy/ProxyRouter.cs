using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy;

public enum RouteKind { Reject, Tunnel, Direct }

/// <summary>Host is normalised for Tunnel/Direct; Reason is descriptive for Reject.</summary>
public sealed record RouteDecision(RouteKind Kind, string Host, int Port, string? Reason);

/// <summary>
/// The routing decision on the user's side (docs/protocol.md and plan 7.5): an address literal (any address) or a local name -> a local 403 refusal (defence in depth;
/// an IP cannot be listed to begin with); allowed -> through the tunnel; otherwise -> a direct connection from the user's machine (ADR-0004).
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

    /// <summary>localhost and *.localhost are refused locally (the browser does not usually pass them to the proxy, but defence in depth is required).</summary>
    public static bool IsLocalName(string normalizedHost)
        => normalizedHost == "localhost" || normalizedHost.EndsWith(".localhost", StringComparison.Ordinal);

    /// <summary>Does the name match any entry in the list regardless of the port (for the 307 answer to http://)?</summary>
    public static bool HostIsAllowlisted(string normalizedHost, IAllowlist allowlist)
        => allowlist.HostMatches(normalizedHost);

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
        307 => "Temporary Redirect",
        400 => "Bad Request",
        403 => "Forbidden",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error",
    };
}
