using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;

namespace RouteBridge.App.ViewModels;

/// <summary>
/// Guest page: the list of available hosts. Refresh reads <c>GET /hosts</c>; <c>hosts.snapshot</c> / <c>hosts.update</c> from the
/// control channel replace the list live. This device is never listed (you cannot browse through yourself).
/// </summary>
public sealed partial class GuestViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly IAuthSession _auth;
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

    public GuestViewModel(IApiClient api, IAuthSession auth, IControlChannel controlChannel, ILogger<GuestViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _controlChannel = controlChannel;
        _logger = logger;
        Hosts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHosts));
        _controlChannel.MessageReceived += OnMessage;
        _auth.Changed += (_, _) => UiThread.Post(() =>
        {
            if (!_auth.IsSignedIn)
            {
                Hosts.Clear();
                SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNotSignedInText);
            }
        });
    }

    public ObservableCollection<HostListItem> Hosts { get; } = new();

    public bool HasHosts => Hosts.Count > 0;

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!_auth.IsSignedIn)
        {
            SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNotSignedInText);
            return;
        }

        IsRefreshing = true;
        try
        {
            _logger.LogInformation("Guest: refreshing hosts via GET /hosts (control channel {State})", _controlChannel.State);
            var hosts = await _api.GetHostsAsync(ct).ConfigureAwait(true);
            ApplyHosts(hosts);
            SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNoHostsText);
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Guest: hosts unavailable: {Reason}", ex.Message);
            Hosts.Clear();
            SetEmptyState(Strings.GuestHostsUnavailableTitle, Strings.GuestHostsUnavailableText);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Guest: GET /hosts failed: {Status} {Code}", (int)ex.StatusCode, ex.Code);
            Hosts.Clear();
            SetEmptyState(Strings.GuestHostsUnavailableTitle, string.Format(CultureInfo.CurrentCulture, Strings.GuestHostsErrorFormat, ex.Message));
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Replaces the list with the server's full snapshot (call on the UI thread). This device is filtered out.</summary>
    public void ApplyHosts(IReadOnlyList<HostInfoDto> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var self = _auth.DeviceId;
        var selected = SelectedHost?.DeviceId;

        Hosts.Clear();
        foreach (var host in hosts)
        {
            if (self is not null && host.DeviceId == self)
            {
                continue;
            }

            Hosts.Add(new HostListItem(host.DeviceId, host.UserDisplayName, host.DeviceName, host.Reachable));
        }

        SelectedHost = Hosts.FirstOrDefault(h => h.DeviceId == selected);
        _logger.LogInformation("Guest: {HostCount} hosts listed", Hosts.Count);
    }

    private void OnMessage(ControlMessage message)
    {
        if (message is HostsMessage hosts)
        {
            UiThread.Post(() =>
            {
                ApplyHosts(hosts.Hosts);
                SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNoHostsText);
            });
        }
    }

    private void SetEmptyState(string title, string text)
    {
        EmptyStateTitle = title;
        EmptyStateText = text;
    }

    // WEEK 4: RequestSessionAsync(HostListItem host, int durationMin) → SessionCoordinator.RequestSessionAsync + the waiting screen with Cancel.
}
