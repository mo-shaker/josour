using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Settings;

namespace Josour.App.ViewModels;

/// <summary>
/// Sign-in window: server URL (persisted in settings), email, password, progress and inline error text - and, on a server
/// that takes registrations, "Create an account" (ADR-0017).
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IApiClient _api;
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

    /// <summary>The window is creating an account rather than signing in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningIn))]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private bool _isRegistering;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _displayName = string.Empty;

    /// <summary>Something to say that is not an error ("waiting for approval").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _infoText;

    public LoginViewModel(IApiClient api, IAuthSession auth, IAppSettingsStore settings, IAuthFlow flow, ILogger<LoginViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _settings = settings;
        _flow = flow;
        _logger = logger;
        _serverUrl = settings.Current.ServerUrl;
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasInfo => !string.IsNullOrEmpty(InfoText);

    public bool IsSigningIn => !IsRegistering;

    public bool IsIdle => !IsBusy;

    /// <summary>Raised after a successful sign-in (the flow has already switched to the main window).</summary>
    public event EventHandler? SignedIn;

    private bool CanSignIn() =>
        !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Email) && Password.Length > 0;

    private bool CanRegister() => CanSignIn() && IsRegistering && !string.IsNullOrWhiteSpace(DisplayName);

    [RelayCommand]
    private void ToggleRegistering()
    {
        IsRegistering = !IsRegistering;
        ErrorText = null;
        InfoText = null;
    }

    /// <summary>
    /// <c>POST /auth/register</c>, then: an active account signs in straight away through the normal path; a pending one
    /// says so and goes back to the sign-in form for later.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync(CancellationToken ct)
    {
        ErrorText = null;
        InfoText = null;
        if (!await SaveServerUrlAsync(ct))
        {
            return;
        }

        IsBusy = true;
        (RegistrationResult result, string? message) outcome;
        try
        {
            outcome = await AccountRegistration.RegisterAsync(_api, Email, Password, DisplayName, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _logger.LogInformation("Registration: {Result}", outcome.result);
        switch (outcome.result)
        {
            case RegistrationResult.SignInNow:
                IsRegistering = false;
                await SignInAsync(ct);
                break;
            case RegistrationResult.Pending:
                IsRegistering = false;
                Password = string.Empty;
                InfoText = outcome.message;
                break;
            default:
                ErrorText = outcome.message;
                break;
        }
    }

    private async Task<bool> SaveServerUrlAsync(CancellationToken ct)
    {
        if (!Infrastructure.Api.ServerUrl.TryNormalize(ServerUrl, out var baseUri) || !Infrastructure.Api.ServerUrl.IsAllowed(baseUri))
        {
            ErrorText = Strings.LoginErrorServerUrl;
            return false;
        }

        var normalized = baseUri.ToString().TrimEnd('/');
        if (!string.Equals(normalized, _settings.Current.ServerUrl, StringComparison.Ordinal))
        {
            await _settings.SaveAsync(_settings.Current with { ServerUrl = normalized }, ct);
            ServerUrl = normalized;
        }

        return true;
    }

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
            ErrorText = string.Format(UiFlow.Culture, Strings.LoginErrorGenericFormat, ex.Message);
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
            ApiErrorCodes.AccountPending => Strings.LoginErrorAccountPending,
            ApiErrorCodes.AccountRejected => Strings.LoginErrorAccountRejected,
            ApiErrorCodes.UpgradeRequired => Strings.ErrorUpgradeRequired,
            ApiErrorCodes.DeviceRevoked => Strings.LoginErrorDeviceRevoked,
            ApiErrorCodes.Unauthorized => Strings.LoginErrorDeviceRejected, // 401 on login = the stored device secret was refused
            ApiErrorCodes.RateLimited => Strings.LoginErrorRateLimited,
            ApiErrorCodes.ValidationError => string.Format(UiFlow.Culture, Strings.LoginErrorValidationFormat, ex.Message),
            _ => string.Format(UiFlow.Culture, Strings.LoginErrorGenericFormat, ex.Code),
        };
    }
}
