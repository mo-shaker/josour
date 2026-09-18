using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Josour.App.Models;
using Josour.App.Services.Notifications;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Infrastructure.Session;

namespace Josour.App.Services;

/// <summary>
/// Presents an incoming request two ways at once: a notification, and <see cref="IncomingRequestWindow"/> (top-most,
/// with the system alert sound). Whichever answers first wins; notification buttons are routed here through
/// <see cref="IToastActivationHandler"/>.
/// <para>
/// The window is the path that must always work. On Windows a toast can be silently suppressed by Focus Assist; on
/// macOS an unbundled app gets no buttons on its banner at all (see <c>MacNotifier</c>). So the notification is how
/// the app asks for attention, and the window is how it takes the answer.
/// </para>
/// </summary>
public sealed class IncomingRequestPresenter : IIncomingRequestPresenter, IDisposable
{
    private sealed record ActiveRequest(IncomingRequestViewModel ViewModel, IncomingRequestWindow Window);

    private readonly INotifier _toasts;
    private readonly IAttentionSound _sound;
    private readonly IToastActivationHandler _activation;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IncomingRequestPresenter> _logger;
    private readonly Func<DateTimeOffset> _serverNow;
    private readonly ConcurrentDictionary<Guid, ActiveRequest> _active = new();

    /// <param name="coordinator">
    /// Only for its clock: <c>expires_at</c> is server time, and the coordinator is what knows the offset
    /// from <c>hello.ack</c>. Comparing against the local clock instead disabled Accept and Reject on a
    /// host whose machine ran fast.
    /// </param>
    public IncomingRequestPresenter(
        INotifier toasts,
        IAttentionSound sound,
        IToastActivationHandler activation,
        ILoggerFactory loggerFactory,
        ILogger<IncomingRequestPresenter> logger,
        SessionCoordinator coordinator)
    {
        _toasts = toasts;
        _sound = sound;
        _activation = activation;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _serverNow = (coordinator ?? throw new ArgumentNullException(nameof(coordinator))).ServerTimeNow;
        _activation.Activated += OnToastActivated;
    }

    public async Task<IncomingRequestAnswer> PresentAsync(IncomingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ViewModel owns a DispatcherTimer and the Window is a visual: both must be created on the UI thread.
        var active = await UiThread.InvokeAsync(() =>
        {
            var viewModel = new IncomingRequestViewModel(
                request, _loggerFactory.CreateLogger<IncomingRequestViewModel>(), _serverNow);
            var window = new IncomingRequestWindow(viewModel);
            return new ActiveRequest(viewModel, window);
        }).ConfigureAwait(false);

        _active[request.RequestId] = active;
        try
        {
            await _toasts.ShowIncomingRequestAsync(request.GuestName, request.GuestDevice, request.DurationMinutes, request.RequestId, ct).ConfigureAwait(false);

            await UiThread.InvokeAsync(() =>
            {
                _sound.Play();
                active.Window.Show();
                active.Window.Activate();
            }).ConfigureAwait(false);

            using var cancellation = ct.Register(() => active.ViewModel.Dismiss());
            var answer = await active.ViewModel.Completion.ConfigureAwait(false);
            _logger.LogInformation("Incoming request {RequestId} decided: {Decision}", request.RequestId, answer.Decision);
            return answer;
        }
        finally
        {
            _active.TryRemove(request.RequestId, out _);
            _toasts.ClearIncomingRequest(request.RequestId);
        }
    }

    public void Expire(Guid requestId)
    {
        if (_active.TryGetValue(requestId, out var active))
        {
            active.ViewModel.Expire(); // marshals itself to the window's dispatcher
        }
    }

    private void OnToastActivated(object? sender, ToastActivation e)
    {
        if (e.RequestId is not Guid requestId || !_active.TryGetValue(requestId, out var active))
        {
            if (e.RequestId is not null)
            {
                _logger.LogInformation("Toast activation for request {RequestId} arrived after it was already decided", e.RequestId);
            }

            return;
        }

        // The handler already marshalled us to the UI thread.
        switch (e.Action)
        {
            case NotificationActions.Accept:
                active.ViewModel.AcceptCommand.Execute(null);
                break;
            case NotificationActions.Reject:
                active.ViewModel.RejectCommand.Execute(null);
                break;
            default:
                active.Window.Show();
                active.Window.Activate();
                break;
        }
    }

    public void Dispose() => _activation.Activated -= OnToastActivated;
}
