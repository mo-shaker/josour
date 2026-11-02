using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Localization;
using Josour.Mobile.Platform;
using Josour.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.ViewModels;

/// <summary>The one screen of the phone: sign-in until there is an account, then the host page.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAuthSession _auth;
    private readonly MobileChannelConnector _connector;
    private readonly IContactsStore _contacts;
    private readonly IMobileNotifier _notifier;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    private bool _isSignedIn;

    [ObservableProperty]
    private bool _isStarting = true;

    public MainViewModel(
        IAuthSession auth,
        MobileChannelConnector connector,
        IContactsStore contacts,
        IMobileNotifier notifier,
        SignInViewModel signIn,
        HostViewModel host,
        ILogger<MainViewModel> logger)
    {
        _auth = auth;
        _connector = connector;
        _contacts = contacts;
        _notifier = notifier;
        _logger = logger;
        SignIn = signIn;
        Host = host;
        SignIn.SignedIn += () => _ = OnSignedInAsync();
        _auth.SignedOut += (_, e) => Dispatcher.UIThread.Post(() => _ = HandleSignedOutAsync(e.Reason));
    }

    public SignInViewModel SignIn { get; }

    public HostViewModel Host { get; }

    public bool IsSignedOut => !IsSignedIn && !IsStarting;

    partial void OnIsStartingChanged(bool value) => OnPropertyChanged(nameof(IsSignedOut));

    /// <summary>Silent sign-in with the stored device and refresh token, as the desktop does at start-up.</summary>
    public async Task StartAsync()
    {
        try
        {
            if (await _auth.TryRestoreAsync(CancellationToken.None))
            {
                _logger.LogInformation("Session restored for {Email}", _auth.CurrentUser?.Email);
                await OnSignedInAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Silent sign-in failed unexpectedly");
        }
        finally
        {
            IsStarting = false;
        }
    }

    private async Task OnSignedInAsync()
    {
        IsSignedIn = true;
        try
        {
            await _connector.ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The status line says "Offline"; the channel retries on its own and on every network change.
            _logger.LogWarning(ex, "Control channel could not be opened after sign-in");
        }

        try
        {
            await _contacts.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load the contact list");
        }

        Host.RestoreAvailability();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        Host.StopForSignOut();
        await _connector.DisconnectAsync();
        await _auth.SignOutAsync(CancellationToken.None); // raises SignedOut
    }

    private async Task HandleSignedOutAsync(SignOutReason reason)
    {
        _logger.LogInformation("Signed out ({Reason})", reason);
        Host.StopForSignOut();
        await _connector.DisconnectAsync();
        IsSignedIn = false;

        var notice = reason switch
        {
            SignOutReason.SessionExpired => Tr.Get(UiStringKeys.SignedOutSessionExpired),
            SignOutReason.DeviceRevoked => Tr.Get(UiStringKeys.SignedOutDeviceRevoked),
            SignOutReason.AccountDisabled => Tr.Get(UiStringKeys.SignedOutAccountDisabled),
            _ => null,
        };
        if (notice is not null)
        {
            _notifier.ShowInfo(Tr.Get(UiStringKeys.SignedOutTitle), notice);
        }
    }
}
