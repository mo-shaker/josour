using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>
/// A transport that accepts a <b>hostname</b> in <see cref="CandidateEndpoint.Ip"/>, resolves it, and then delegates the connection to an inner transport.
///
/// <para>
/// <see cref="DirectTransport"/> refuses anything but an IP address literal, and that is not excess strictness but a security control:
/// the candidates arrive from <c>session.peer_endpoint</c>, that is, from the other side through the server, and the contract requires them to be
/// literals (<c>docs/ws-protocol.md</c> section 5). Had it accepted names, the peer could make us resolve whatever it chose.
/// </para>
/// <para>
/// The relay's address is an entirely different case: it arrives in <c>session.created.relay</c> from <b>our own server</b>, and it is a name by
/// design (<c>RELAY_HOST</c> in ADR-0009). So the difference between the two paths is a difference of source rather than of strictness, and that is why it was solved with a second transport
/// rather than by widening the first.
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

        // One name may resolve to both IPv6 and IPv4, and the first of them may not be reachable from this network. We try them
        // in order within the overall timeout instead of stopping at the first failure.
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
