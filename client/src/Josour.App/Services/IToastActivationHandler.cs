using Josour.App.Services.Notifications;
namespace Josour.App.Services;

/// <summary>A toast (body or button) was clicked. <see cref="Action"/> is <c>accept</c>, <c>reject</c> or <c>open</c>.</summary>
public sealed record ToastActivation(
    string Action,
    Guid? RequestId,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyDictionary<string, string> UserInput);

/// <summary>Receives raw toast activations from <see cref="Notifications.INotifier"/> and republishes them on the UI thread.</summary>
public interface IToastActivationHandler
{
    event EventHandler<ToastActivation>? Activated;

    void Handle(string arguments, IReadOnlyDictionary<string, string> userInput);
}
