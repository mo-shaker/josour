using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary><see cref="RelayTransport"/>'s configuration. The token comes from the backend server and is requested on every connection so it can be refreshed.</summary>
public sealed record RelayTransportOptions
{
    /// <summary>The same session id as in session.created (the relay pairs the two sides by it).</summary>
    public required Guid SessionId { get; init; }

    public required TunnelRole Role { get; init; }

    /// <summary>A session token signed by the backend server. It is not logged and does not appear in the diagnostics.</summary>
    public required Func<CancellationToken, ValueTask<string>> TokenProvider { get; init; }

    /// <summary>The relay's address from the configuration. null = the candidate passed to ConnectAsync is used as it is.</summary>
    public CandidateEndpoint? Endpoint { get; init; }

    /// <summary>The timeout for exchanging the preamble and the reply once TCP succeeds.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The transport the socket to the relay is opened with (Direct by default; replaceable in the tests).</summary>
    public ITunnelTransport Inner { get; init; } = new DirectTransport();
}

/// <summary>
/// A transport over a relay: it opens TCP to the relay, sends a <see cref="RelayProtocol"/> preamble (the magic, the version, the role, the session_id, the signed token),
/// and waits for <see cref="RelayStatus.Paired"/> before handing the stream upwards. Everything after that is opaque bytes: TLS and the authentication run
/// between the two machines exactly as on the direct path (ADR-0003 item 5: the transport behind <see cref="ITunnelTransport"/> from day one).
///
/// Its effect on the rest of the system when enabled: zero. Just <c>TunnelSessionOptions.Transport = new RelayTransport(...)</c>, and neither
/// <see cref="SymmetricConnector"/> nor <see cref="Mux.NerdbankMux"/> nor the egress policy changes.
///
/// WEEK 5/6: the relay service itself (token verification, pairing, byte pumping, the limits) is built only if the ADR-0003 gate closes.
/// The tests today run a fake in-process relay that pairs two sockets.
///
/// An open point for week 5/6 (it does not touch today's contract): on the direct path the listener is the TLS server, but over the relay both sides are
/// connectors, so whoever presents the certificate must be pinned down. Most likely: the host is always the TLS server (as it is the authority on acceptance), and the preamble already carries the role
/// so nothing in this file changes. That is why today's tests check the opaque bytes rather than TLS over the relay.
/// </summary>
public sealed class RelayTransport : ITunnelTransport
{
    private readonly RelayTransportOptions _options;

    public RelayTransport(RelayTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.TokenProvider);
        ArgumentNullException.ThrowIfNull(options.Inner);
    }

    public string Name => "relay";

    /// <summary>The actual relay address used for a given candidate (the configuration beats the candidate).</summary>
    public CandidateEndpoint Target(CandidateEndpoint candidate) => _options.Endpoint ?? candidate;

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var stream = await _options.Inner.ConnectAsync(Target(endpoint), timeout, ct).ConfigureAwait(false);
        try
        {
            var token = await _options.TokenProvider(ct).ConfigureAwait(false);
            var preamble = RelayProtocol.BuildPreamble(_options.SessionId, _options.Role, token);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_options.HandshakeTimeout);
                try
                {
                    await stream.WriteAsync(preamble, cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("timed out sending the relay preamble");
                }
            }

            var status = await RelayProtocol.ReadReplyAsync(stream, _options.HandshakeTimeout, ct).ConfigureAwait(false);
            if (status != RelayStatus.Paired) throw new RelayRejectedException(status);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
