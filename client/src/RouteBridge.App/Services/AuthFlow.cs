using Microsoft.Extensions.Logging;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.App.Services;

/// <summary><see cref="IAuthFlow"/>; also reacts to <see cref="IAuthSession.SignedOut"/> (expired/rejected refresh) by showing the sign-in window.</summary>
public sealed class AuthFlow : IAuthFlow, IDisposable
{
    private readonly IAuthSession _auth;
    private readonly IShellService _shell;
    private readonly ControlChannelConnector _connector;
    private readonly IToastService _toasts;
    private readonly IAppSettingsStore _settings;
    private readonly StartupOptions _options;
    private readonly ILogger<AuthFlow> _logger;

    public AuthFlow(
        IAuthSession auth,
        IShellService shell,
        ControlChannelConnector connector,
        IToastService toasts,
        IAppSettingsStore settings,
        StartupOptions options,
        ILogger<AuthFlow> logger)
    {
        _auth = auth;
        _shell = shell;
        _connector = connector;
        _toasts = toasts;
        _settings = settings;
        _options = options;
        _logger = logger;
        _auth.SignedOut += OnSignedOut;
    }

    /// <summary>
    /// Where a start-up lands is decided by <see cref="StartupPlanner"/>, not here — the rule is testable and the same one
    /// applies wherever it is asked. What changed in week 6: a machine with no usable server address goes to the guided
    /// first run instead of to the sign-in window with an empty address field.
    /// </summary>
    public async Task RunStartupAsync(CancellationToken ct)
    {
        bool restored;
        try
        {
            restored = await _auth.TryRestoreAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Silent sign-in failed unexpectedly");
            restored = false;
        }

        var credentialsStored = restored;
        if (!restored)
        {
            try
            {
                credentialsStored = await _auth.HasStoredCredentialsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not tell whether this machine has ever been signed in");
            }
        }

        var plan = StartupPlanner.Decide(_settings.Current, credentialsStored, restored);
        _logger.LogInformation(
            "Start-up plan: {Destination} (server={ServerUrl} freshInstall={Fresh})",
            plan.Destination,
            string.IsNullOrEmpty(plan.ServerUrl) ? "(not set)" : plan.ServerUrl,
            plan.IsFreshInstall);

        switch (plan.Destination)
        {
            case StartupDestination.FirstRun:
                _shell.ShowFirstRunWindow();
                return;

            case StartupDestination.SignIn:
                _shell.ShowLoginWindow();
                return;

            default:
                _logger.LogInformation("Session restored for {Email}", _auth.CurrentUser?.Email);
                await ConnectQuietlyAsync(ct);
                ShowOrStayInTray();
                return;
        }
    }

    /// <summary>
    /// <c>--minimized</c> (the Run-key launch) OR the "start minimized" setting — which until week 6 was written to the
    /// settings file by nothing and read by nobody.
    /// </summary>
    private void ShowOrStayInTray()
    {
        if (_options.StartMinimized || _settings.Current.StartMinimized)
        {
            _logger.LogInformation(
                "Started minimized to the tray (switch={FromSwitch} setting={FromSetting})",
                _options.StartMinimized,
                _settings.Current.StartMinimized);
            return;
        }

        _shell.ShowMainWindow();
    }

    public async Task CompleteSignInAsync(CancellationToken ct)
    {
        await ConnectQuietlyAsync(ct);
        _shell.CloseLoginWindow();
        _shell.ShowMainWindow();
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        await _connector.DisconnectAsync();
        await _auth.SignOutAsync(ct); // raises SignedOut → OnSignedOut shows the sign-in window
    }

    private async Task ConnectQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await _connector.ConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal for sign-in: the status line shows "Offline"; week 3's channel reconnects on its own.
            _logger.LogWarning(ex, "Control channel could not be opened after sign-in");
        }
    }

    private void OnSignedOut(object? sender, SignedOutEventArgs e) => UiThread.Post(() => _ = HandleSignedOutAsync(e.Reason));

    private async Task HandleSignedOutAsync(SignOutReason reason)
    {
        _logger.LogInformation("Signed out ({Reason})", reason);
        try
        {
            await _connector.DisconnectAsync();
        }
        finally
        {
            _shell.HideMainWindow();
            _shell.ShowLoginWindow();
        }

        var notice = reason switch
        {
            SignOutReason.SessionExpired => Strings.SignedOutSessionExpired,
            SignOutReason.DeviceRevoked => Strings.SignedOutDeviceRevoked,
            SignOutReason.AccountDisabled => Strings.SignedOutAccountDisabled,
            _ => null,
        };
        if (notice is not null)
        {
            _toasts.ShowInfo(Strings.SignedOutTitle, notice);
        }
    }

    public void Dispose() => _auth.SignedOut -= OnSignedOut;
}
