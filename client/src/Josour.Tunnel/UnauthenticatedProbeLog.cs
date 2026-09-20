using System.Net;
using System.Net.Sockets;

namespace Josour.Tunnel;

/// <summary>
/// The counter of inbound connections that did not pass <c>AUTH1</c> on the tunnel's listener (docs/protocol.md section 2).
///
/// <para><b>Why it exists:</b> the tunnel's listener is open between <c>session.created</c> and <c>session.connected</c> only, and it is the one place
/// where the system sees an unauthorised attempt to reach the machine's port. The server cannot observe it itself (none of it passes
/// through it), so the client reports it in <c>POST /api/v1/diagnostics</c> under the keys reserved in <c>docs/api.md</c>:
/// <c>listener_unauthenticated</c>, <c>listener_port</c> and <c>unauthenticated_peers</c>.</para>
///
/// <para><b>What is stored:</b> a counter and distinct source addresses capped at <see cref="MaxPeers"/>, and nothing else. No payloads, no source ports and no timestamps
/// (the privacy requirement in section 15 of the product document, and an explicit condition of <c>docs/api.md</c>). Mapped
/// <c>::ffff:a.b.c.d</c> addresses are normalised to IPv4 so the same address does not appear twice.</para>
///
/// <para><b>What is not counted:</b> a connection that passed AUTH1 (even if it lost the race: <c>superseded</c>), and a connection we cancelled ourselves when
/// another connection won or the connect window closed. The purpose is a security signal with no false positives, so doubt is resolved in favour of silence.</para>
///
/// Thread-safe: the accept loop and the authentication handlers write to it in parallel.
/// </summary>
public sealed class UnauthenticatedProbeLog
{
    /// <summary>The cap on distinct addresses stored (the same limit as in <c>docs/api.md</c>: <c>unauthenticated_peers</c> ≤ 10).</summary>
    public const int MaxPeers = 10;

    private readonly object _gate = new();
    private readonly List<string> _peers = new(MaxPeers);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _count;
    private int _distinctPeers;

    /// <summary>The number of inbound connections closed before passing AUTH1.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The number of distinct addresses actually seen (it may exceed <see cref="MaxPeers"/> while the list does not).</summary>
    public int DistinctPeers => Volatile.Read(ref _distinctPeers);

    /// <summary>The first <see cref="MaxPeers"/> distinct addresses in order of appearance.</summary>
    public IReadOnlyList<string> Peers
    {
        get { lock (_gate) return _peers.ToArray(); }
    }

    /// <summary>Records an unauthenticated attempt from <paramref name="remote"/> (null = an unknown address: counted but not recorded).</summary>
    public void Record(IPAddress? remote)
    {
        Interlocked.Increment(ref _count);
        if (remote is null) return;
        var text = Normalize(remote);
        lock (_gate)
        {
            if (!_seen.Add(text)) return;
            _distinctPeers = _seen.Count;
            if (_peers.Count < MaxPeers) _peers.Add(text);
        }
    }

    /// <summary>The shape sent in <c>data</c> at <c>POST /api/v1/diagnostics</c>, or null if no attempt occurred.</summary>
    public IReadOnlyDictionary<string, object?>? ToDiagnostics(int listenerPort)
    {
        var count = Count;
        if (count <= 0) return null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["listener_unauthenticated"] = count,
            ["listener_port"] = listenerPort,
            ["unauthenticated_peers"] = Peers.ToList(),
        };
    }

    /// <summary><c>::ffff:a.b.c.d</c> -> <c>a.b.c.d</c>, and the scope id is stripped from local IPv6 addresses.</summary>
    private static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes());
        return address.ToString();
    }
}
