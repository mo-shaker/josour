using Microsoft.Extensions.Logging;
using Josour.App.Services.Notifications;

namespace Josour.App.Services;

/// <summary>Week 1: parses the arguments, logs them, and raises <see cref="Activated"/> on the UI thread for ViewModels/presenters.</summary>
public sealed class ToastActivationHandler : IToastActivationHandler
{
    private readonly ILogger<ToastActivationHandler> _logger;

    public ToastActivationHandler(ILogger<ToastActivationHandler> logger)
    {
        _logger = logger;
    }

    public event EventHandler<ToastActivation>? Activated;

    public void Handle(string arguments, IReadOnlyDictionary<string, string> userInput)
    {
        var parsed = NotificationActions.Parse(arguments);

        parsed.TryGetValue(NotificationActions.ActionKey, out var action);
        Guid? requestId = parsed.TryGetValue(NotificationActions.RequestIdKey, out var rawId) && Guid.TryParse(rawId, out var id) ? id : null;

        _logger.LogInformation("Toast activated: action={Action} requestId={RequestId} inputs={InputCount}", action ?? "(none)", requestId, userInput.Count);

        var activation = new ToastActivation(action ?? string.Empty, requestId, parsed, userInput);

        // Activation arrives on a COM/background thread; deliver on the UI thread so subscribers can touch bindings and windows.
        UiThread.Post(() => Activated?.Invoke(this, activation));
    }
}
