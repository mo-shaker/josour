using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace RouteBridge.App.Services;

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
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in ToastArguments.Parse(arguments ?? string.Empty))
            {
                parsed[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogWarning(ex, "Toast activation carried unparsable arguments");
        }

        parsed.TryGetValue(ToastService.ActionKey, out var action);
        Guid? requestId = parsed.TryGetValue(ToastService.RequestIdKey, out var rawId) && Guid.TryParse(rawId, out var id) ? id : null;

        _logger.LogInformation("Toast activated: action={Action} requestId={RequestId} inputs={InputCount}", action ?? "(none)", requestId, userInput.Count);

        var activation = new ToastActivation(action ?? string.Empty, requestId, parsed, userInput);

        // Activation arrives on a COM/background thread; deliver on the UI thread so subscribers can touch bindings and windows.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Activated?.Invoke(this, activation);
        }
        else
        {
            dispatcher.BeginInvoke(() => Activated?.Invoke(this, activation));
        }
    }
}
