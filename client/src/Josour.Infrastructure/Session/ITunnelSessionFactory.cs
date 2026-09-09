using Josour.Core.Allowlist;
using Josour.Core.Browser;
using Josour.Core.Tunnel;

namespace Josour.Infrastructure.Session;

/// <summary>
/// Everything <see cref="SessionCoordinator"/> knows when <c>session.created</c> arrives; the factory turns it into a
/// <c>TunnelSessionOptions</c> and a real <c>Josour.Tunnel.TunnelSession</c>.
/// <para>
/// The request deliberately carries only <c>Josour.Core</c> types so that this assembly (and its tests) never has to
/// reference <c>Josour.Tunnel</c>/<c>Egress</c>/<c>Proxy</c>: the App owns that wiring
/// (<c>HostEgress = EgressTunnelAdapter.Create</c>, <c>GuestProxy = ctx =&gt; ProxyTunnelAdapter.Create(ctx, browser)</c>).
/// </para>
/// </summary>
/// <param name="Material">
/// session id, role, the 32-byte secret, expires_at, same_public_ip and peer_public_ip from <c>session.created</c>.
/// The tunnel owns <see cref="SessionMaterial.Secret"/> and zeroes it during cleanup, so the array must not be reused.
/// </param>
/// <param name="Allowlist">The allow-list at the session's <c>allowlist_version</c> (<c>GET /domains</c>).</param>
/// <param name="AllowedPorts"><c>hello.ack.settings.allowed_ports</c>.</param>
/// <param name="OurPublicIp"><c>hello.ack.public_ip</c>: the <c>public</c> candidate, and a blocked address on the host.</param>
/// <param name="Browser">
/// The work-browser session, passed to the guest proxy for the owner-PID check. Not launched by the tunnel:
/// the coordinator launches it after <c>session.active</c> and closes it through <paramref name="CloseBrowserAsync"/>.
/// </param>
/// <param name="CloseBrowserAsync">Cleanup step 2 of docs/protocol.md section 7, run inside <c>ITunnelSession.EndAsync</c>.</param>
public sealed record TunnelSessionRequest(
    SessionMaterial Material,
    IAllowlist Allowlist,
    IReadOnlyList<int> AllowedPorts,
    string? OurPublicIp,
    IBrowserSession Browser,
    Func<CancellationToken, Task> CloseBrowserAsync,
    /// <summary>From <c>session.created.relay</c>; null when the deployment has no relay (ADR-0009).</summary>
    RelayEndpointInfo? Relay = null);

/// <summary>Creates the tunnel for one session. The App supplies the real implementation; tests substitute a fake.</summary>
public interface ITunnelSessionFactory
{
    ITunnelSession Create(TunnelSessionRequest request);
}
