using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Josour.Core.Tunnel;
using Josour.Tunnel.Auth;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Certificates;
using Josour.Tunnel.Tls;

namespace Josour.Tunnel;

/// <summary>An authenticated connection (TLS + AUTH1/AUTH2 complete). The Stream is an SslStream that owns the socket.</summary>
public sealed record AuthenticatedConnection(Stream Stream, CandidateType WinnerType, string TlsVersion, bool Inbound, string RemoteDescription) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed record SymmetricConnectOutcome(TunnelConnectResult Result, AuthenticatedConnection? Connection, IReadOnlyDictionary<string, object?> Diagnostics);

/// <summary>The relay path: the transport that speaks its protocol and the address that arrived in session.created.relay (ADR-0009).</summary>
public sealed record RelayLeg(ITunnelTransport Transport, CandidateEndpoint Endpoint);

/// <summary>
/// It coordinates docs/protocol.md section 2 steps 3-6 for both roles: accepting inbound connections (as the TLS server with our certificate) and connecting to all of
/// the other side's candidates in parallel (as the TLS client pinned to its fingerprint), then authenticating per the logical role. The first connection that passes authentication wins;
/// the rest are closed and the listener is stopped.
///
/// Who decides the winner: the host is the authority (session.connected). To stop each side keeping a different connection, the host sends AUTH2 only on
/// the first connection that passes AUTH1 and closes the rest with no reply (which section 4 permits). So the guest sees only one AUTH2, and its
/// "first authenticated" connection is the host's connection itself. Week 4 adds an explicit confirmation handshake if real trials show it is needed.
/// </summary>
public sealed class SymmetricConnector
{
    public static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ceiling on inbound-connection rows in the diagnostics. The project expects no more than the number of the other side's candidates, but any party on
    /// the network can open connections on the port during the connect window. With no ceiling the list grows without bound (memory), and
    /// <c>session.connect_failed</c> swells past the 64 KB limit in <c>docs/api.md</c> so the whole diagnostic is refused. The excess stays counted in
    /// <c>inbound_attempts</c> and <c>inbound_unauthenticated</c> and in <see cref="TunnelListener.Probes"/>.
    /// </summary>
    public const int MaxRecordedInboundAttempts = 16;

    private readonly SessionMaterial _material;
    private readonly SessionCertificate _certificate;
    private readonly TunnelListener _listener;
    private readonly ITunnelTransport _transport;
    private readonly IReadOnlyList<CandidateEndpoint> _ourCandidates;
    private readonly RelayLeg? _relay;

    private readonly List<Attempt> _attempts = new();
    private readonly object _gate = new();
    private int _claimed;
    private int _started;
    private int _inboundSeen;
    private int _inboundDropped;
    /// <summary>Raised once at the first connection that passes authentication, and never goes back. See <see cref="IsUnauthenticatedProbe"/>.</summary>
    private int _authenticatedAny;

    public SymmetricConnector(SessionMaterial material, SessionCertificate ourCertificate, TunnelListener listener, ITunnelTransport transport, IReadOnlyList<CandidateEndpoint>? ourCandidates = null, RelayLeg? relay = null)
    {
        _relay = relay;
        _material = material ?? throw new ArgumentNullException(nameof(material));
        _certificate = ourCertificate ?? throw new ArgumentNullException(nameof(ourCertificate));
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ourCandidates = ourCandidates ?? Array.Empty<CandidateEndpoint>();
    }

    /// <summary>
    /// Any attempt advancing to a new stage ("tls", then "auth", then "ok"). It is raised from several threads and may repeat;
    /// <see cref="TunnelSession"/> uses it to reflect Connecting -> Authenticating in StateChanged. The listener's errors are swallowed.
    /// </summary>
    public event Action<string>? StageReached;

    /// <summary>Called once. When it returns, the listener is stopped and every other attempt is closed.</summary>
    public async Task<SymmetricConnectOutcome> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("ConnectAsync may be called only once");

        byte[] peerFp;
        try { peerFp = Convert.FromHexString(peer.CertFingerprintSha256Hex); }
        catch (FormatException e) { throw new ArgumentException("peer certificate fingerprint is not valid hex", nameof(peer), e); }
        if (peerFp.Length != TlsChannel.FingerprintLength) throw new ArgumentException("peer certificate fingerprint must be 32 bytes", nameof(peer));
        var ourFp = _certificate.FingerprintSha256;

        var clock = Stopwatch.StartNew();
        var winner = new TaskCompletionSource<(AuthenticatedConnection Connection, long Ms)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _listener.StartAsync((socket, lct) => HandleInboundAsync(socket, peerFp, ourFp, winner, clock, lct), cts.Token).ConfigureAwait(false);
        // ADR-0009: the relay is attempted in parallel with direct rather than after it. Direct is faster when it succeeds, and the relay always works,
        // so the race gives the best available without waiting for one of them to fail.
        var dials = peer.Candidates.Select(c => DialAsync(c, peerFp, ourFp, winner, clock, cts.Token))
            .Append(_relay is null ? Task.CompletedTask : DialRelayAsync(_relay, timeout, peerFp, ourFp, winner, clock, cts.Token))
            .ToArray();

        try
        {
            await winner.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A timeout or an external cancellation
        }

        cts.Cancel();
        await _listener.StopAsync().ConfigureAwait(false);
        try { await Task.WhenAll(dials).ConfigureAwait(false); } catch { /* the attempts swallow their own errors */ }

        var elapsed = (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue);
        if (winner.Task.IsCompletedSuccessfully)
        {
            var (connection, ms) = winner.Task.Result;
            var result = new TunnelConnectResult(true, connection.WinnerType, (int)Math.Min(ms, int.MaxValue), connection.TlsVersion, null);
            return new SymmetricConnectOutcome(result, connection, BuildDiagnostics(timeout, elapsed, connection));
        }

        var reason = ct.IsCancellationRequested ? "cancelled" : "timeout";
        var failed = new TunnelConnectResult(false, null, elapsed, null, reason);
        return new SymmetricConnectOutcome(failed, null, BuildDiagnostics(timeout, elapsed, null));
    }

    // ---------- inbound ----------

    private async Task HandleInboundAsync(Socket socket, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var remote = socket.RemoteEndPoint as IPEndPoint;
        var local = socket.LocalEndPoint as IPEndPoint;
        var remoteAddress = remote?.Address is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : remote?.Address;
        var attempt = new Attempt("inbound", ClassifyInbound(local), remoteAddress?.ToString() ?? "?", remote?.Port ?? 0);
        Record(attempt);
        try
        {
            Stream raw;
            try { raw = new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
            await AuthenticateAsync(raw, inbound: true, tlsServer: true, attempt, peerFp, ourFp, winner, clock, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            attempt.Error = Describe(e);
            attempt.PeerClosed = e is AuthFailedException { PeerClosed: true };
        }
        finally
        {
            attempt.Ms = sw.ElapsedMilliseconds;
            if (IsUnauthenticatedProbe(attempt)) _listener.Probes.Record(remoteAddress);
        }
    }

    /// <summary>
    /// Is this inbound connection an unauthorised attempt to reach our port (<c>docs/api.md</c>: <c>listener_unauthenticated</c>)?
    ///
    /// <para>The signal cannot bear one false positive: one in every successful session robs it of its meaning entirely. So three cases
    /// are excluded, each of them describing <b>the legitimate peer</b> rather than an attacker:</para>
    /// <list type="number">
    ///   <item><b>What we cancelled ourselves</b> (<c>cancelled</c>): another connection winning, or the connect window closing.</item>
    ///   <item><b>What the other side closed with no reply</b> (<see cref="AuthFailedException.PeerClosed"/>): this is literally what
    ///     the host does to the losing connection in section 2 step 5 — "closes any other connection with no reply". And the symmetric connection always opens
    ///     two connections between the two machines, so one of them loses <b>in every successful session</b>.</item>
    ///   <item><b>What failed after we had authenticated someone</b>: from that moment every failure is race debris rather than evidence.</item>
    /// </list>
    /// <para>The price is accepted and known: a port scanner that completes the TLS handshake and then closes without speaking is not counted. What is counted includes
    /// every scan that does not complete TLS (the overwhelming majority), every timeout, and <b>every verification failure</b> — the strongest evidence possible.</para>
    /// </summary>
    private bool IsUnauthenticatedProbe(Attempt attempt)
    {
        if (attempt.Authenticated || attempt.Error is null) return false;
        if (attempt.Error == "cancelled" || attempt.PeerClosed) return false;
        return Volatile.Read(ref _authenticatedAny) == 0;
    }

    // ---------- dial ----------

    private async Task DialAsync(CandidateEndpoint candidate, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempt = new Attempt("dial", candidate.Type, candidate.Ip, candidate.Port) { Stage = "connect" };
        Record(attempt);
        try
        {
            var raw = await _transport.ConnectAsync(candidate, DialTimeout, ct).ConfigureAwait(false);
            await AuthenticateAsync(raw, inbound: false, tlsServer: false, attempt, peerFp, ourFp, winner, clock, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            attempt.Error = Describe(e);
        }
        finally
        {
            attempt.Ms = sw.ElapsedMilliseconds;
        }
    }

    /// <summary>The connection attempt over the relay: one transport and one address, with no candidates and no listener.</summary>
    private async Task DialRelayAsync(RelayLeg relay, TimeSpan timeout, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempt = new Attempt("relay", CandidateType.Relay, relay.Endpoint.Ip, relay.Endpoint.Port) { Stage = "connect" };
        Record(attempt);
        try
        {
            // The full timeout rather than DialTimeout: the relay's reply does not arrive until the peer arrives too, and it may be as late as the whole
            // connect window. Limiting it to five seconds would have dropped every session whose two sides do not start together.
            var raw = await relay.Transport.ConnectAsync(relay.Endpoint, timeout, ct).ConfigureAwait(false);
            // docs/protocol.md section 3: there is no listener here, so the host is always the TLS server on this path.
            await AuthenticateAsync(raw, inbound: false, tlsServer: _material.Role == TunnelRole.Host, attempt, peerFp, ourFp, winner, clock, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            attempt.Error = Describe(e);
        }
        finally
        {
            attempt.Ms = sw.ElapsedMilliseconds;
        }
    }

    // ---------- shared ----------

    /// <summary>
    /// <paramref name="tlsServer"/> is deliberately separate from <paramref name="inbound"/>. On the direct path they are the same thing
    /// (the listener is the server), but over the relay there is no listener: both sides connect outbound, so if the rule stayed derived from
    /// "who connected" both would wait for the other's handshake forever. The rule there: the host is always the server (docs/protocol.md section 3).
    /// </summary>
    private async Task AuthenticateAsync(Stream raw, bool inbound, bool tlsServer, Attempt attempt, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        Stage(attempt, "tls");
        var tls = tlsServer
            ? await TlsChannel.AuthenticateAsServerAsync(raw, _certificate.Certificate, TlsTimeout, ct).ConfigureAwait(false)
            : await TlsChannel.AuthenticateAsClientAsync(raw, peerFp, TlsTimeout, ct).ConfigureAwait(false);

        // listener_cert_fp = the certificate of whoever is the TLS server in this connection, whoever opened the socket.
        var listenerFp = tlsServer ? ourFp : peerFp;
        Stage(attempt, "auth");
        try
        {
            if (_material.Role == TunnelRole.Host)
            {
                var auth1 = await AuthHandshake.VerifyAuth1Async(tls.Stream, _material, listenerFp, AuthTimeout, ct).ConfigureAwait(false);
                attempt.Authenticated = true; // it passed AUTH1: not an unauthorised attempt even if it loses the race afterwards
                Volatile.Write(ref _authenticatedAny, 1);
                if (!TryClaim()) throw new SupersededException();
                try
                {
                    await AuthHandshake.SendAuth2Async(tls.Stream, _material, listenerFp, auth1, AuthTimeout, ct).ConfigureAwait(false);
                }
                catch
                {
                    Volatile.Write(ref _claimed, 0);
                    throw;
                }
            }
            else
            {
                await AuthHandshake.SendAuth1AndVerifyAuth2Async(tls.Stream, _material, listenerFp, AuthTimeout, ct).ConfigureAwait(false);
                attempt.Authenticated = true;
                Volatile.Write(ref _authenticatedAny, 1);
                if (!TryClaim()) throw new SupersededException();
            }

            var connection = new AuthenticatedConnection(tls.Stream, attempt.Type ?? CandidateType.Public, tls.TlsVersion, inbound, $"{attempt.Ip}:{attempt.Port}");
            Stage(attempt, "ok");
            if (!winner.TrySetResult((connection, clock.ElapsedMilliseconds)))
                throw new SupersededException();
        }
        catch
        {
            await tls.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    private void Stage(Attempt attempt, string stage)
    {
        attempt.Stage = stage;
        try { StageReached?.Invoke(stage); } catch { /* the listener is responsible for its own errors */ }
    }

    /// <summary>
    /// Adds the attempt to the diagnostics. Every dial row is recorded because their number = the number of the other side's candidates; the inbound ones
    /// are bounded by <see cref="MaxRecordedInboundAttempts"/> because their source is the network rather than us (see the constant's comment).
    /// </summary>
    private void Record(Attempt attempt)
    {
        if (attempt.Direction == "inbound" && Interlocked.Increment(ref _inboundSeen) > MaxRecordedInboundAttempts)
        {
            Interlocked.Increment(ref _inboundDropped);
            return;
        }
        lock (_gate) _attempts.Add(attempt);
    }

    /// <summary>
    /// Classifying an inbound connection by the local address it arrived at: it matches our lan/v6 candidates; otherwise the connection came through NAT,
    /// where upnp and public cannot be told apart from the receiver's side, so we prefer upnp if we have a mapping.
    /// </summary>
    private CandidateType? ClassifyInbound(IPEndPoint? local)
    {
        if (local is null) return null;
        var address = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var text = address.ToString();
        foreach (var c in _ourCandidates)
            if (c.Type is CandidateType.Lan or CandidateType.V6 && c.Ip == text) return c.Type;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && LocalNetwork.IsGlobalIPv6(address)) return CandidateType.V6;
        if (address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address)) return CandidateType.Lan;
        if (_ourCandidates.Any(c => c.Type == CandidateType.Upnp)) return CandidateType.Upnp;
        return CandidateType.Public;
    }

    private IReadOnlyDictionary<string, object?> BuildDiagnostics(TimeSpan timeout, int elapsedMs, AuthenticatedConnection? connection)
    {
        Attempt[] attempts;
        lock (_gate) attempts = _attempts.ToArray();
        var rows = attempts.Select(a => new Dictionary<string, object?>
        {
            ["direction"] = a.Direction,
            ["type"] = a.Type is null ? null : CandidateTypeNames.ToWire(a.Type.Value),
            ["ip"] = a.Ip,
            ["port"] = a.Port,
            ["ms"] = a.Ms,
            ["stage"] = a.Stage,
            ["error"] = a.Error,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["role"] = _material.Role == TunnelRole.Host ? "host" : "guest",
            ["timeout_ms"] = (long)timeout.TotalMilliseconds,
            ["elapsed_ms"] = elapsedMs,
            ["listener_port"] = _listener.Port,
            ["inbound_attempts"] = _listener.InboundAttempts,
            ["inbound_rejected"] = _listener.RejectedOverCapacity,
            // Inbound connections that did not pass AUTH1, and the number of rows dropped from the list because of the ceiling (both counted, not recorded).
            ["inbound_unauthenticated"] = _listener.Probes.Count,
            ["inbound_dropped"] = Volatile.Read(ref _inboundDropped),
            ["candidates"] = rows,
            ["winner"] = connection is null ? null : $"{(connection.Inbound ? "inbound" : "dial")} {CandidateTypeNames.ToWire(connection.WinnerType)} {connection.RemoteDescription}",
            ["winner_type"] = connection is null ? null : CandidateTypeNames.ToWire(connection.WinnerType),
            ["tls_version"] = connection?.TlsVersion,
        };
    }

    // No secrets and no payloads in the text; the exception messages here are descriptive only.
    private static string Describe(Exception e) => e switch
    {
        SupersededException => "superseded",
        OperationCanceledException => "cancelled",
        TimeoutException t => $"timeout: {t.Message}",
        TlsTooOldException => "tls_too_old",
        AuthFailedException a => $"auth_failed: {a.Message}",
        AuthenticationException a => $"tls: {a.Message}",
        SocketException s => $"socket: {s.SocketErrorCode}",
        IOException io when io.InnerException is SocketException s => $"socket: {s.SocketErrorCode}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };

    private sealed class SupersededException : Exception
    {
        public SupersededException() : base("another connection already won") { }
    }

    private sealed class Attempt
    {
        public Attempt(string direction, CandidateType? type, string ip, int port)
        {
            Direction = direction;
            Type = type;
            Ip = ip;
            Port = port;
        }

        public string Direction { get; }
        public CandidateType? Type { get; }
        public string Ip { get; }
        public int Port { get; }
        public long Ms { get; set; }
        public string? Stage { get; set; }
        public string? Error { get; set; }

        /// <summary>It passed AUTH1 (the host) or verified AUTH2 (the guest), even if it lost the race afterwards.</summary>
        public bool Authenticated { get; set; }

        /// <summary>The other side closed, or the link dropped, before the authentication message completed (not a verification failure).</summary>
        public bool PeerClosed { get; set; }
    }
}
