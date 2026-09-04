using Microsoft.Extensions.Logging;
using RouteBridge.Infrastructure.Api;

namespace RouteBridge.App.Services;

/// <summary><see cref="IAuthFlow"/>; also reacts to <see cref="IAuthSession.SignedOut"/> (expired/rejected refresh) by showing the sign-in window.</summary>
public sealed class AuthFlow : IAuthFlow, IDisposable
{
    private readonly IAuthSession _auth;
    private readonly IShellService _shell;
    private readonly ControlChannelConnector _connector;
    private readonly IToastService _toasts;
    private readonly StartupOptions _options;
    private readonly ILogger<AuthFlow> _logger;

    public AuthFlow(
        IAuthSession auth,
        IShellService shell,
        ControlChannelConnector connector,
        IToastService toasts,
        StartupOptions options,
        ILogger<AuthFlow> logger)
    {
        _auth = auth;
        _shell = shell;
        _connector = connector;
        _toasts = toasts;
        _options = options;
        _logger = logger;
        _auth.SignedOut += OnSignedOut;
    }

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

        if (!restored)
        {
            _logger.LogInformation("No restorable session: showing the sign-in window");
            _shell.ShowLoginWindow();
            return;
        }

        _logger.LogInformation("Session restored for {Email}", _auth.CurrentUser?.Email);
        await ConnectQuietlyAsync(ct);

        if (_options.StartMinimized)
        {
            _logger.LogInformation("Started minimized to the tray");
        }
        else
        {
            _shell.ShowMainWindow();
        }
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
