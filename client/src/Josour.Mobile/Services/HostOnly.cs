using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;

namespace Josour.Mobile.Services;

/// <summary>
/// The guest side's two hooks, answered for a phone that never is one: no system proxy to point anywhere
/// (<see cref="IsSupported"/> false, which is what <c>SessionCoordinator.IsGuestRoleSupported</c> reads) and no check page.
/// </summary>
public sealed class HostOnlySystemProxyGuard : ISystemProxyGuard, ICheckPageOpener
{
    public static HostOnlySystemProxyGuard Instance { get; } = new();

    public bool IsSupported => false;
    public bool IsApplied => false;
    public Task ApplyAsync(int port, IReadOnlyList<string> extraBypass, CancellationToken ct) =>
        throw new NotSupportedException("This phone can only host a session.");
    public Task<bool> RestoreAsync(CancellationToken ct) => Task.FromResult(false);

    public bool OpenOnConnect => false;
    public bool TryOpen(string url) => false;
}
