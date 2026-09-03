using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.Core.Control;

namespace RouteBridge.App.ViewModels;

/// <summary>Guest page: list of available hosts (empty this week) and Refresh.</summary>
public sealed partial class GuestViewModel : ObservableObject
{
    private readonly IControlChannel _controlChannel;
    private readonly ILogger<GuestViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _emptyStateTitle = Strings.GuestNoHostsTitle;

    [ObservableProperty]
    private string _emptyStateText = Strings.GuestNoHostsText;

    [ObservableProperty]
    private HostListItem? _selectedHost;

    public GuestViewModel(IControlChannel controlChannel, ILogger<GuestViewModel> logger)
    {
        _controlChannel = controlChannel;
        _logger = logger;
        Hosts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHosts));
    }

    public ObservableCollection<HostListItem> Hosts { get; } = new();

    public bool HasHosts => Hosts.Count > 0;

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            _logger.LogInformation("Guest: refresh requested (control channel {State})", _controlChannel.State);

            // WEEK 2: GuestViewModel.LoadHostsAsync — hosts arrive as hosts.snapshot / hosts.update on IControlChannel.MessageReceived
            // (HostsMessage → ApplyHosts). Until the WS snapshot is in, Refresh re-reads REST GET /hosts through ApiClient.GetHostsAsync.
            await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Replaces the list with the server's full snapshot (called on the UI thread).</summary>
    public void ApplyHosts(IReadOnlyList<HostInfoDto> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        Hosts.Clear();
        foreach (var host in hosts)
        {
            Hosts.Add(new HostListItem(host.DeviceId, host.UserDisplayName, host.DeviceName, host.Reachable));
        }
    }

    // WEEK 2: GuestViewModel.RequestSessionAsync(HostListItem host, int durationMin) — IControlChannel.RequestAsync(new RequestCreateMessage(...)).
}
