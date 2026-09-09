using System.Collections.Concurrent;
using System.Media;
using System.Windows;
using Microsoft.Extensions.Logging;
using Josour.App.Models;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Infrastructure.Session;

namespace Josour.App.Services;

/// <summary>
/// Presents an incoming request two ways at once, because Focus Assist can silently suppress toasts:
/// a toast with Accept/Reject buttons, and <see cref="IncomingRequestWindow"/> (top-most, plays the system exclamation sound).
/// Whichever answers first wins; toast buttons are routed here through <see cref="IToastActivationHandler"/>.
/// </summary>
public sealed class IncomingRequestPresenter : IIncomingRequestPresenter, IDisposable
{
    private sealed record ActiveRequest(IncomingRequestViewModel ViewModel, IncomingRequestWindow Window);

    private readonly IToastService _toasts;
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
        IToastService toasts,
        IToastActivationHandler activation,
        ILoggerFactory loggerFactory,
        ILogger<IncomingRequestPresenter> logger,
        SessionCoordinator coordinator)
    {
        _toasts = toasts;
        _activation = activation;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _serverNow = (coordinator ?? throw new ArgumentNullException(nameof(coordinator))).ServerTimeNow;
        _activation.Activated += OnToastActivated;
    }

    public async Task<IncomingRequestDecision> PresentAsync(IncomingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dispatcher = Application.Current.Dispatcher;

        // The ViewModel owns a DispatcherTimer and the Window is a WPF object: both must be created on the UI thread.
        var active = await dispatcher.InvokeAsync(() =>
        {
            var viewModel = new IncomingRequestViewModel(
                request, _loggerFactory.CreateLogger<IncomingRequestViewModel>(), _serverNow);
            var window = new IncomingRequestWindow(viewModel);
            return new ActiveRequest(viewModel, window);
        });

        _active[request.RequestId] = active;
        try
        {
            await _toasts.ShowIncomingRequestAsync(request.GuestName, request.GuestDevice, request.DurationMinutes, request.RequestId, ct).ConfigureAwait(false);

            await dispatcher.InvokeAsync(() =>
            {
                PlayAttentionSound();
                active.Window.Show();
                active.Window.Activate();
            });

            using var cancellation = ct.Register(() => active.ViewModel.Dismiss());
            var decision = await active.ViewModel.Completion.ConfigureAwait(false);
            _logger.LogInformation("Incoming request {RequestId} decided: {Decision}", request.RequestId, decision);
            return decision;
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
            case ToastService.ActionAccept:
                active.ViewModel.AcceptCommand.Execute(null);
                break;
            case ToastService.ActionReject:
                active.ViewModel.RejectCommand.Execute(null);
                break;
            default:
                active.Window.Show();
                active.Window.Activate();
                break;
        }
    }

    private void PlayAttentionSound()
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not play the attention sound");
        }
    }

    public void Dispose() => _activation.Activated -= OnToastActivated;
}
