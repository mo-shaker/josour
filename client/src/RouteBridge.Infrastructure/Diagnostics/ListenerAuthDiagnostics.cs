using System.Globalization;
using System.Net;

namespace RouteBridge.Infrastructure.Diagnostics;

/// <summary>
/// What the host saw on its tunnel listener that never became a session: connections that reached the port during the
/// connect window and failed <c>AUTH1</c> (docs/protocol.md section 2). It is the one unauthorized-access attempt the
/// system can observe at all — the server never sees the host's port — which is why docs/api.md reserves keys for it inside
/// <c>POST /diagnostics</c> and turns a positive count into a <c>security_events</c> row.
/// </summary>
/// <param name="UnauthenticatedCount">Connections that reached the listener and failed AUTH1. Always &gt; 0 when reported.</param>
/// <param name="ListenerPort">The local listener port, when the tunnel reported one.</param>
/// <param name="Peers">Distinct source IPs, at most <see cref="ListenerAuthDiagnostics.MaxPeers"/>; may be empty.</param>
public sealed record ListenerAuthReport(int UnauthenticatedCount, int? ListenerPort, IReadOnlyList<string> Peers)
{
    /// <summary>
    /// Exactly the reserved keys of docs/api.md and nothing else. Optional keys are omitted rather than sent as null, and
    /// there is deliberately no room here for a payload, a host name or a timestamp: a counter and source addresses only.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToDiagnosticsData()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ListenerAuthDiagnostics.CountKey] = UnauthenticatedCount,
        };

        if (ListenerPort is int port)
        {
            data[ListenerAuthDiagnostics.PortKey] = port;
        }

        if (Peers.Count > 0)
        {
            data[ListenerAuthDiagnostics.PeersKey] = Peers.ToArray();
        }

        return data;
    }
}

/// <summary>
/// <b>ADAPTER POINT (single, deliberate).</b> Reads the unauthenticated-listener counters out of
/// <c>ITunnelSession.Diagnostics</c> — a free <c>IReadOnlyDictionary&lt;string, object?&gt;</c> owned by track B — and turns
/// them into the reserved wire keys of docs/api.md. Everything that couples this layer to that dictionary is in this file.
/// <para>
/// Track B's <c>UnauthenticatedProbeLog</c> writes the contract's own names, so the source keys and the wire keys are the
/// same three strings today. They are still listed separately here because they are two different contracts: if the tunnel
/// ever renames or restructures its diagnostics, only <see cref="CountSourceKeys"/>, <see cref="PortSourceKeys"/> and
/// <see cref="PeerSourceKeys"/> move, and the wire stays frozen where docs/api.md froze it.
/// </para>
/// <para>
/// The tunnel also publishes <c>unauthenticated_peers_distinct</c> (how many distinct addresses were seen before the cap).
/// It is deliberately NOT forwarded: docs/api.md reserves three keys, and a diagnostics row that carries a fourth invites
/// the server to start depending on something no contract promises.
/// </para>
/// <para>
/// Values are re-validated rather than forwarded: the count must be a positive integer, and every peer must parse as an IP
/// literal. That is not defensiveness about types, it is the privacy rule — the contract says a counter and source
/// addresses only, so anything that is not an address does not leave this machine even if it turns up in that dictionary.
/// </para>
/// </summary>
public static class ListenerAuthDiagnostics
{
    // ---- the wire (docs/api.md, frozen) ----

    /// <summary>Reserved key: the number of connections that failed AUTH1. A positive value is also a security signal.</summary>
    public const string CountKey = "listener_unauthenticated";

    /// <summary>Reserved optional key: the listener port those connections reached.</summary>
    public const string PortKey = "listener_port";

    /// <summary>Reserved optional key: distinct source IPs, at most <see cref="MaxPeers"/>.</summary>
    public const string PeersKey = "unauthenticated_peers";

    /// <summary>The contract's cap on <see cref="PeersKey"/>.</summary>
    public const int MaxPeers = 10;

    // ---- the adapter: what to read out of ITunnelSession.Diagnostics ----

    /// <summary>Keys that may hold the count (track B's <c>UnauthenticatedProbeLog</c> writes the first).</summary>
    public static IReadOnlyList<string> CountSourceKeys { get; } = new[] { CountKey };

    /// <summary>Keys that may hold the listener port (written by the tunnel's <c>PrepareAsync</c>).</summary>
    public static IReadOnlyList<string> PortSourceKeys { get; } = new[] { PortKey };

    /// <summary>Keys that may hold the source addresses.</summary>
    public static IReadOnlyList<string> PeerSourceKeys { get; } = new[] { PeersKey };

    /// <summary>
    /// True when the tunnel reported at least one unauthenticated hit. Never throws and never reports a zero count:
    /// "nobody knocked" is not worth a row, and docs/api.md only treats a positive value as a signal.
    /// </summary>
    public static bool TryRead(IReadOnlyDictionary<string, object?>? diagnostics, out ListenerAuthReport report)
    {
        report = null!;
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return false;
        }

        if (ReadInt(diagnostics, CountSourceKeys) is not int count || count <= 0)
        {
            return false;
        }

        var port = ReadInt(diagnostics, PortSourceKeys);
        if (port is < 1 or > 65535)
        {
            port = null;
        }

        report = new ListenerAuthReport(count, port, ReadPeers(diagnostics));
        return true;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> diagnostics, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (diagnostics.TryGetValue(key, out var raw) && TryToInt(raw, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryToInt(object? raw, out int value)
    {
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;
            case short s:
                value = s;
                return true;
            case double d when d >= int.MinValue && d <= int.MaxValue && Math.Floor(d) == d:
                value = (int)d;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Distinct IP literals, in the order seen, capped at <see cref="MaxPeers"/>. Anything else is dropped silently.</summary>
    private static IReadOnlyList<string> ReadPeers(IReadOnlyDictionary<string, object?> diagnostics)
    {
        foreach (var key in PeerSourceKeys)
        {
            if (!diagnostics.TryGetValue(key, out var raw) || raw is null)
            {
                continue;
            }

            var candidates = raw switch
            {
                string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                System.Collections.IEnumerable items => items.Cast<object?>().Select(x => x?.ToString()).ToArray(),
                _ => new[] { raw.ToString() },
            };

            var peers = new List<string>(MaxPeers);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !IPAddress.TryParse(candidate.Trim(), out var address))
                {
                    continue; // not an address: the contract allows nothing else here
                }

                var normalized = address.ToString();
                if (!peers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    peers.Add(normalized);
                }

                if (peers.Count == MaxPeers)
                {
                    break;
                }
            }

            if (peers.Count > 0)
            {
                return peers;
            }
        }

        return Array.Empty<string>();
    }
}
