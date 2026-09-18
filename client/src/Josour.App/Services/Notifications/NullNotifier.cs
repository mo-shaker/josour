using Microsoft.Extensions.Logging;

namespace Josour.App.Services.Notifications;

/// <summary>
/// Notifies nobody — Linux, and any platform without an implementation.
/// <para>
/// It logs each notification it swallows rather than returning silently. A build where notifications quietly do
/// nothing is one where "the host was never told" looks exactly like "the host ignored it", and the log is the only
/// place that difference survives.
/// </para>
/// </summary>
public sealed class NullNotifier : INotifier
{
    private readonly ILogger<NullNotifier> _logger;

    public NullNotifier(ILogger<NullNotifier> logger) => _logger = logger;

    public void RegisterActivation() =>
        _logger.LogInformation("This platform has no notification support; the request window is the only prompt");

    public Task ShowIncomingRequestAsync(string guestName, string deviceName, int durationMin, Guid requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation("No notification shown for request {RequestId}; the request window carries it", requestId);
        return Task.CompletedTask;
    }

    public void ShowInfo(string title, string text) =>
        _logger.LogInformation("No notification shown: {Title} - {Text}", title, text);

    public void ClearIncomingRequest(Guid requestId)
    {
    }
}
