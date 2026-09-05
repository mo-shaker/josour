namespace RouteBridge.App.ViewModels;

/// <summary>One row of the Guest page's host list (shape of <c>hosts.snapshot</c> entries).</summary>
public sealed record HostListItem(Guid DeviceId, string UserDisplayName, string DeviceName, bool? Reachable)
{
    /// <summary>The device name as the list shows it: a technical identifier, isolated so it reads left to right.</summary>
    public string DeviceNameText => UiFlow.Ltr(DeviceName);

    public string ReachabilityText => Reachable switch
    {
        true => Strings.HostReachable,
        false => Strings.HostUnreachable,
        null => Strings.HostReachabilityUnknown,
    };
}
