using System.Net;
using Josour.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Egress;

/// <summary>
/// The host's side of <see cref="TunnelSession"/>: it builds an <see cref="EgressPolicy"/> from the session's context (the list, the ports, the resolver,
/// the machine's blocked addresses), wires an <see cref="OpenHandler"/> to the mux's acceptor, and exposes the counters and the domains.
///
/// Why a factory rather than direct construction inside TunnelSession: <c>Josour.Egress</c> depends on <c>Josour.Tunnel</c>
/// (ADR-0006: the mux lives there), so the direction cannot be reversed. Track C passes <c>EgressTunnelAdapter.Create</c> as it is.
/// </summary>
public sealed class EgressTunnelAdapter : ITunnelEgress
{
    private readonly OpenHandler _handler;

    public EgressTunnelAdapter(OpenHandler handler)
        => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>The production use: <c>HostEgress = EgressTunnelAdapter.Create</c>.</summary>
    public static ITunnelEgress Create(TunnelEgressContext context) => Create(context, null, null);

    /// <param name="addressBlocker">
    /// Replacing the blocked check for one address; for in-process E2E tests only (to permit 127.0.0.1). null = the IpRangePolicy policy.
    /// </param>
    /// <param name="limiter">
    /// Custom stream limits; null = <see cref="TunnelEgressContext.MaxConcurrentStreams"/> (the one that goes with the RTT-derived
    /// window: 256, 128 or 64) and 50 opens a second as the contract has them.
    /// </param>
    public static ITunnelEgress Create(TunnelEgressContext context, Func<IPAddress, bool>? addressBlocker, StreamLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policy = new EgressPolicy(context.Allowlist, new EgressPolicyOptions
        {
            AllowedPorts = context.AllowedPorts,
            Resolver = context.Resolver,
            LocalAddresses = context.BlockedLocalAddresses,
            AddressBlocker = addressBlocker,
        });
        return new EgressTunnelAdapter(new OpenHandler(policy, limiter ?? new StreamLimiter(context.MaxConcurrentStreams)));
    }

    /// <summary>The underlying handler (the successful and failed open counters, the limits) for whoever needs finer detail than the interface gives.</summary>
    public OpenHandler Handler => _handler;

    public long BytesUp => _handler.Counter.Up;
    public long BytesDown => _handler.Counter.Down;
    public IReadOnlyCollection<string> Domains => _handler.Domains.Snapshot();

    public void Attach(IMuxAcceptor acceptor) => _handler.Attach(acceptor);

    /// <summary>No resources of its own: the streams are owned by the channels and closed by the mux when the tunnel closes.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
