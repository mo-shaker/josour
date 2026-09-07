using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Settings;

namespace Josour.App.ViewModels;

/// <summary>The three steps of the guided first run.</summary>
public enum FirstRunStep
{
    /// <summary>The server address, with a real check against it.</summary>
    Server,

    /// <summary>Sign in to the address that was just proven to answer.</summary>
    SignIn,

    /// <summary>What this machine looks like as a host, before the user ever touches "available".</summary>
    Readiness,
}

/// <summary>
/// The first run on a machine that has never been configured. Until week 6 such a machine was dropped at the sign-in
/// window with an empty server field: a question the user cannot answer without being told, asked in a form that does not
/// explain it, next to credentials that cannot be checked until the address is right.
/// <para>
/// The order is the only one that can work — address, then sign in — and the address step insists on a real answer from
/// <c>GET /healthz</c> before it lets the user past, because every failure after that point would otherwise be blamed on
/// the password. The third step is not a gate at all: it is the readiness of this machine as a host (the firewall rule and
/// whether a VPN holds the outbound route), shown once, before anyone has the chance to make the device available and
/// wonder later why the other side could not reach it or why the sites saw a foreign address.
/// </para>
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    /// <summary>Steps shown in the "step N of M" line.</summary>
    public const int StepCount = 3;

    private readonly IAppSettingsStore _settings;
    private readonly IServerCheck _serverCheck;
    private readonly IAuthSession _auth;
    private readonly IAuthFlow _flow;
    private readonly HostReadinessMonitor _readiness;
    private readonly ILogger<FirstRunViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStep), nameof(IsSignInStep), nameof(IsReadinessStep), nameof(StepText), nameof(StepTitle), nameof(StepDescription))]
    private FirstRunStep _step = FirstRunStep.Server;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueFromServerCommand))]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _email = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueFromServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _isMessageAnError;

    [ObservableProperty]
    private bool _isReadinessChecking = true;

    [ObservableProperty]
    private string _firewallText = Strings.ReadinessChecking;

    [ObservableProperty]
    private bool _isFirewallWarning;

    [ObservableProperty]
    private string _vpnText = Strings.ReadinessChecking;

    [ObservableProperty]
    private bool _isVpnWarning;

    public FirstRunViewModel(
        IAppSettingsStore settings,
        IServerCheck serverCheck,
        IAuthSession auth,
        IAuthFlow flow,
        HostReadinessMonitor readiness,
        ILogger<FirstRunViewModel> logger)
    {
        _settings = settings;
        _serverCheck = serverCheck;
        _auth = auth;
        _flow = flow;
        _readiness = readiness;
        _logger = logger;
        _serverUrl = settings.Current.ServerUrl;
    }

    public bool IsServerStep => Step == FirstRunStep.Server;

    public bool IsSignInStep => Step == FirstRunStep.SignIn;

    public bool IsReadinessStep => Step == FirstRunStep.Readiness;

    public bool IsIdle => !IsBusy;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string StepText => string.Format(UiFlow.Culture, Strings.FirstRunStepFormat, (int)Step + 1, StepCount);

    public string StepTitle => Step switch
    {
        FirstRunStep.Server => Strings.FirstRunServerTitle,
        FirstRunStep.SignIn => Strings.FirstRunSignInTitle,
        _ => Strings.FirstRunReadyTitle,
    };

    public string StepDescription => Step switch
    {
        FirstRunStep.Server => Strings.FirstRunServerText,
        FirstRunStep.SignIn => Strings.FirstRunSignInText,
        _ => Strings.FirstRunReadyText,
    };

    /// <summary>Raised when the window should close (Finish, or the user gave up).</summary>
    public event EventHandler? Closed;

    // ---- step 1: the address ----

    private bool CanContinueFromServer() => !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl);

    /// <summary>
    /// Validate, check, store, advance. The check is a gate on purpose: an address that does not answer cannot sign
    /// anyone in, and letting the user past here would turn a wrong address into "wrong password" one screen later.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinueFromServer))]
    private async Task ContinueFromServerAsync(CancellationToken ct)
    {
        IsBusy = true;
        Message = null;
        try
        {
            var result = await _serverCheck.CheckAsync(ServerUrl, ct);
            if (!result.IsOk || result.NormalizedUrl is not { } normalized)
            {
                ShowServerResult(result);
                return;
            }

            ServerUrl = normalized;
            await _settings.SaveAsync(_settings.Current with { ServerUrl = normalized }, ct);
            _logger.LogInformation("First run: server address set to {ServerUrl}", normalized);

            Message = null;
            Step = FirstRunStep.SignIn;
        }
        catch (OperationCanceledException)
        {
            // the window closed while checking
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First run: the server step failed");
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- step 2: sign in ----

    private bool CanSignIn() => !IsBusy && !string.IsNullOrWhiteSpace(Email) && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken ct)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await _auth.SignInAsync(Email.Trim(), Password, ct);
            Password = string.Empty;
            _logger.LogInformation("First run: signed in");

            Step = FirstRunStep.Readiness;
            _ = RefreshReadinessAsync(force: false);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("First run: sign-in rejected ({Status} {Code})", (int)ex.StatusCode, ex.Code);
            ShowError(LoginViewModel.MapError(ex));
        }
        catch (ApiUnavailableException ex)
        {
            // The address answered /healthz a moment ago, so this is a real outage rather than a typo.
            _logger.LogWarning("First run: sign-in failed: {Reason}", ex.Message);
            ShowError(Strings.LoginErrorUnavailable);
        }
        catch (OperationCanceledException)
        {
            // the window closed while signing in
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First run: sign-in failed unexpectedly");
            ShowError(string.Format(UiFlow.Culture, Strings.LoginErrorGenericFormat, ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BackToServer()
    {
        Message = null;
        Step = FirstRunStep.Server;
    }

    // ---- step 3: readiness ----

    [RelayCommand]
    private Task RecheckReadinessAsync() => RefreshReadinessAsync(force: true);

    private async Task RefreshReadinessAsync(bool force)
    {
        UiThread.Post(() =>
        {
            IsReadinessChecking = true;
            FirewallText = Strings.ReadinessChecking;
            VpnText = Strings.ReadinessChecking;
            IsFirewallWarning = false;
            IsVpnWarning = false;
        });

        var readiness = await _readiness.RefreshAsync(force, CancellationToken.None).ConfigureAwait(false);
        UiThread.Post(() => ApplyReadiness(readiness));
    }

    /// <summary>
    /// Three states per line, not two: present, missing, and "could not be determined" — which is what a non-Windows or a
    /// locked-down machine answers, and must not be shown as "missing".
    /// </summary>
    private void ApplyReadiness(HostReadiness readiness)
    {
        IsFirewallWarning = readiness.IsFirewallRuleMissing;
        FirewallText = readiness.FirewallRulePresent switch
        {
            true => Strings.ReadinessFirewallOk,
            false => Strings.ReadinessFirewallMissing,
            null => Strings.ReadinessFirewallUnknown,
        };

        IsVpnWarning = readiness.ShouldWarnAboutVpn;
        VpnText = readiness.ShouldWarnAboutVpn
            ? string.Format(UiFlow.Culture, Strings.ReadinessVpnWarningFormat, UiFlow.Ltr(readiness.VpnAdapterName))
            : Strings.ReadinessVpnOk;

        IsReadinessChecking = false;
    }

    /// <summary>Nothing here blocks: the readiness step is a disclosure, and the user finishes whatever it says.</summary>
    [RelayCommand]
    private async Task FinishAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await _flow.CompleteSignInAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "First run: the control channel could not be opened; the app still starts");
        }
        finally
        {
            IsBusy = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowServerResult(ServerCheckResult result)
    {
        var text = result.Status switch
        {
            ServerCheckStatus.InvalidUrl => Strings.SettingsServerInvalidUrl,
            ServerCheckStatus.InsecureScheme => Strings.SettingsServerInsecure,
            ServerCheckStatus.NotJosour => Strings.SettingsServerNotJosour,
            _ => Strings.SettingsServerUnreachable,
        };

        ShowError(string.IsNullOrEmpty(result.Detail)
            ? text
            : text + " " + string.Format(UiFlow.Culture, Strings.SettingsServerDetailFormat, UiFlow.Ltr(result.Detail)));
    }

    private void ShowError(string text)
    {
        Message = text;
        IsMessageAnError = true;
    }
}
