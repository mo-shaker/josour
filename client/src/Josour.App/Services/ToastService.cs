using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Josour.App.Services;

/// <summary>
/// Toasts via Microsoft.Toolkit.Uwp.Notifications. Josour is an unpackaged (Inno Setup) app, so
/// <see cref="ToastNotificationManagerCompat"/> performs the registration Windows needs on first use, under
/// <c>HKCU\Software\Classes</c>: an AppUserModelId (derived from the exe path, with DisplayName/IconUri) and a COM
/// <c>LocalServer32</c> activator CLSID pointing at this exe. That lets Windows COM-activate the running process (or relaunch it
/// with <c>-ToastActivated</c>) when a toast button is clicked. <see cref="ToastNotificationManagerCompat.Uninstall"/> removes both;
/// the installer invokes it on uninstall through the <c>--uninstall-notifications</c> switch (installer/Josour.iss).
/// </summary>
public sealed class ToastService : IToastService, IDisposable
{
    public const string ActionKey = "action";
    public const string RequestIdKey = "request_id";
    public const string ActionAccept = "accept";
    public const string ActionReject = "reject";
    public const string ActionOpen = "open";

    private const string IncomingRequestGroup = "incoming-request";

    private readonly IToastActivationHandler _handler;
    private readonly ILogger<ToastService> _logger;
    private bool _registered;

    public ToastService(IToastActivationHandler handler, ILogger<ToastService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Call once at startup, before any toast is shown, so button clicks reach this process.</summary>
    public void RegisterActivation()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _registered = true;
            _logger.LogInformation(
                "Toast activation registered (launchedByToast={LaunchedByToast})",
                ToastNotificationManagerCompat.WasCurrentProcessToastActivated());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toast activation registration failed; toasts may still show but their buttons will not reach the app");
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in e.UserInput)
        {
            input[pair.Key] = pair.Value?.ToString() ?? string.Empty;
        }

        _handler.Handle(e.Argument ?? string.Empty, input);
    }

    public Task ShowIncomingRequestAsync(string guestName, string deviceName, int durationMin, Guid requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = requestId.ToString("D");
        try
        {
            new ToastContentBuilder()
                .AddArgument(ActionKey, ActionOpen)
                .AddArgument(RequestIdKey, id)
                .AddText(Strings.ToastIncomingRequestTitle)
                .AddText(string.Format(UiFlow.Culture, Strings.ToastIncomingRequestBodyFormat, guestName, UiFlow.Ltr(deviceName), durationMin))
                .AddText(Strings.IncomingRequestWarning)
                .AddButton(new ToastButton()
                    .SetContent(Strings.Accept)
                    .AddArgument(ActionKey, ActionAccept)
                    .AddArgument(RequestIdKey, id))
                .AddButton(new ToastButton()
                    .SetContent(Strings.Reject)
                    .AddArgument(ActionKey, ActionReject)
                    .AddArgument(RequestIdKey, id))
                .SetToastDuration(ToastDuration.Long)
                .Show(toast =>
                {
                    toast.Tag = TagFor(requestId);
                    toast.Group = IncomingRequestGroup;
                    toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(2);
                });

            _logger.LogInformation("Incoming-request toast shown for {RequestId}", requestId);
        }
        catch (Exception ex)
        {
            // Focus Assist, disabled notifications or a broken registration must not block the request flow:
            // IncomingRequestWindow (top-most + sound) is the guaranteed path.
            _logger.LogWarning(ex, "Could not show incoming-request toast for {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    public void ShowInfo(string title, string text)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(text)
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show info toast {Title}", title);
        }
    }

    public void ClearIncomingRequest(Guid requestId)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(TagFor(requestId), IncomingRequestGroup);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove toast for {RequestId}", requestId);
        }
    }

    // Toast tags are limited to 16 characters on older Windows 10 builds.
    private static string TagFor(Guid requestId) => requestId.ToString("N")[..16];

    public void Dispose()
    {
        if (_registered)
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
            _registered = false;
        }
    }
}
