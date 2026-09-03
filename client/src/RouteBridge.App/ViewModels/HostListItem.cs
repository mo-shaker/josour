namespace RouteBridge.App.ViewModels;

/// <summary>One row of the Guest page's host list (shape of <c>hosts.snapshot</c> entries).</summary>
public sealed record HostListItem(Guid DeviceId, string UserDisplayName, string DeviceName, bool? Reachable)
{
    public string ReachabilityText => Reachable switch
    {
        true => Strings.HostReachable,
        false => Strings.HostUnreachable,
        null => Strings.HostReachabilityUnknown,
    };
}
