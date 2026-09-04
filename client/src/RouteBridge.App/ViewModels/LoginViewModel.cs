using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.App.ViewModels;

/// <summary>Sign-in window: server URL (persisted in settings), email, password, progress and inline error text.</summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthSession _auth;
    private readonly IAppSettingsStore _settings;
    private readonly IAuthFlow _flow;
    private readonly ILogger<LoginViewModel> _logger;

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
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private string _signInButtonText = Strings.LoginSignIn;

    public LoginViewModel(IAuthSession auth, IAppSettingsStore settings, IAuthFlow flow, ILogger<LoginViewModel> logger)
    {
        _auth = auth;
        _settings = settings;
        _flow = flow;
        _logger = logger;
        _serverUrl = settings.Current.ServerUrl;
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsIdle => !IsBusy;

    /// <summary>Raised after a successful sign-in (the flow has already switched to the main window).</summary>
    public event EventHandler? SignedIn;

    private bool CanSignIn() =>
        !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Email) && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken ct)
    {
        ErrorText = null;

        if (!Infrastructure.Api.ServerUrl.TryNormalize(ServerUrl, out var baseUri) || !Infrastructure.Api.ServerUrl.IsAllowed(baseUri))
        {
            ErrorText = Strings.LoginErrorServerUrl;
            return;
        }

        IsBusy = true;
        SignInButtonText = Strings.LoginSigningIn;
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

            await _flow.CompleteSignInAsync(ct);
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Sign-in rejected: {Status} {Code}", (int)ex.StatusCode, ex.Code);
            ErrorText = MapError(ex);
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Sign-in failed: {Reason}", ex.Message);
            ErrorText = Strings.LoginErrorUnavailable;
        }
        catch (OperationCanceledException)
        {
            // window closed while signing in
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in failed unexpectedly");
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.LoginErrorGenericFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
            SignInButtonText = Strings.LoginSignIn;
        }
    }

    /// <summary>docs/api.md error codes → user text (Strings.cs).</summary>
    public static string MapError(ApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Code switch
        {
            ApiErrorCodes.InvalidCredentials => Strings.LoginErrorInvalidCredentials,
            ApiErrorCodes.AccountLocked => Strings.LoginErrorAccountLocked,
            ApiErrorCodes.AccountDisabled => Strings.LoginErrorAccountDisabled,
            ApiErrorCodes.DeviceRevoked => Strings.LoginErrorDeviceRevoked,
            ApiErrorCodes.Unauthorized => Strings.LoginErrorDeviceRejected, // 401 on login = the stored device secret was refused
            ApiErrorCodes.RateLimited => Strings.LoginErrorRateLimited,
            ApiErrorCodes.ValidationError => string.Format(CultureInfo.CurrentCulture, Strings.LoginErrorValidationFormat, ex.Message),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.LoginErrorGenericFormat, ex.Code),
        };
    }
}
