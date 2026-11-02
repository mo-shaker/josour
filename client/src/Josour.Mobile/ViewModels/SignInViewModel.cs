using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.ViewModels;

/// <summary>Server address, email and password; the same account and the same errors as the desktop sign-in window.</summary>
public sealed partial class SignInViewModel : ObservableObject
{
    private readonly IAuthSession _auth;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger<SignInViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _serverUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _email = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    public SignInViewModel(IAuthSession auth, IAppSettingsStore settings, ILogger<SignInViewModel> logger)
    {
        _auth = auth;
        _settings = settings;
        _logger = logger;
        _serverUrl = settings.Current.ServerUrl;
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Raised on the UI thread once the account is signed in.</summary>
    public event Action? SignedIn;

    private bool CanSignIn() =>
        !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Email) && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken ct)
    {
        ErrorText = null;
        if (!Infrastructure.Api.ServerUrl.TryNormalize(ServerUrl, out var baseUri) || !Infrastructure.Api.ServerUrl.IsAllowed(baseUri))
        {
            ErrorText = Tr.Get(UiStringKeys.LoginErrorServerUrl);
            return;
        }

        IsBusy = true;
        try
        {
            var normalized = baseUri.ToString().TrimEnd('/');
            if (!string.Equals(normalized, _settings.Current.ServerUrl, StringComparison.Ordinal))
            {
                await _settings.SaveAsync(_settings.Current with { ServerUrl = normalized }, ct);
                ServerUrl = normalized;
            }

            await _auth.SignInAsync(Email.Trim(), Password, ct);
            Password = string.Empty;
            SignedIn?.Invoke();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Sign-in rejected: {Status} {Code}", (int)ex.StatusCode, ex.Code);
            ErrorText = MapError(ex);
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Sign-in failed: {Reason}", ex.Message);
            ErrorText = Tr.Get(UiStringKeys.LoginErrorUnavailable);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in failed unexpectedly");
            ErrorText = Tr.Format(UiStringKeys.LoginErrorGenericFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string MapError(ApiException ex) => ex.Code switch
    {
        ApiErrorCodes.InvalidCredentials => Tr.Get(UiStringKeys.LoginErrorInvalidCredentials),
        ApiErrorCodes.AccountLocked => Tr.Get(UiStringKeys.LoginErrorAccountLocked),
        ApiErrorCodes.AccountDisabled => Tr.Get(UiStringKeys.LoginErrorAccountDisabled),
        ApiErrorCodes.AccountPending => Tr.Get(UiStringKeys.LoginErrorAccountPending),
        ApiErrorCodes.AccountRejected => Tr.Get(UiStringKeys.LoginErrorAccountRejected),
        ApiErrorCodes.UpgradeRequired => Tr.Get(UiStringKeys.ErrorUpgradeRequired),
        ApiErrorCodes.DeviceRevoked => Tr.Get(UiStringKeys.LoginErrorDeviceRevoked),
        ApiErrorCodes.Unauthorized => Tr.Get(UiStringKeys.LoginErrorDeviceRejected),
        ApiErrorCodes.RateLimited => Tr.Get(UiStringKeys.LoginErrorRateLimited),
        ApiErrorCodes.ValidationError => Tr.Format(UiStringKeys.LoginErrorValidationFormat, ex.Message),
        _ => Tr.Format(UiStringKeys.LoginErrorGenericFormat, ex.Code),
    };
}
