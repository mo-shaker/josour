using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Models;

namespace RouteBridge.App.ViewModels;

/// <summary>
/// Drives <see cref="Views.IncomingRequestWindow"/>: request details, the 60 s countdown (from the request's <c>expires_at</c>)
/// and Accept/Reject. Create it on the UI thread — it owns a <see cref="DispatcherTimer"/>; decisions arriving from other threads
/// (toast buttons) are marshalled back to that thread.
/// </summary>
public sealed partial class IncomingRequestViewModel : ObservableObject, IDisposable
{
    private readonly IncomingRequest _request;
    private readonly ILogger<IncomingRequestViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly TaskCompletionSource<IncomingRequestDecision> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(RejectCommand))]
    private bool _isCompleted;

    public IncomingRequestViewModel(IncomingRequest request, ILogger<IncomingRequestViewModel> logger)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Heading = string.Format(CultureInfo.CurrentCulture, Strings.IncomingRequestHeadingFormat, request.GuestName);
        DurationText = string.Format(CultureInfo.CurrentCulture, Strings.DurationMinutesFormat, request.DurationMinutes);

        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
    }

    public Guid RequestId => _request.RequestId;

    public string GuestName => _request.GuestName;

    public string GuestDevice => _request.GuestDevice;

    public int DurationMinutes => _request.DurationMinutes;

    public DateTimeOffset ExpiresAt => _request.ExpiresAt;

    public string Heading { get; }

    public string DurationText { get; }

    public string AllowedSitesText => _request.AllowedSitesSummary;

    public IncomingRequestDecision? Decision { get; private set; }

    /// <summary>Completes with the decision (Accept/Reject/TimedOut/Dismissed).</summary>
    public Task<IncomingRequestDecision> Completion => _completion.Task;

    /// <summary>Raised on the UI thread once a decision exists; the window closes itself on it.</summary>
    public event EventHandler? Completed;

    private bool CanDecide() => !IsCompleted;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Accept() => Complete(IncomingRequestDecision.Accepted);

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject() => Complete(IncomingRequestDecision.Rejected);

    /// <summary>Window closed without an answer, or the app cancelled the prompt.</summary>
    public void Dismiss() => Complete(IncomingRequestDecision.Dismissed);

    /// <summary>Server-side expiry (<c>request.expired</c>).</summary>
    public void Expire() => Complete(IncomingRequestDecision.TimedOut);

    private void Tick()
    {
        var remaining = ExpiresAt - DateTimeOffset.UtcNow;
        var seconds = (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
        if (seconds != SecondsRemaining || CountdownText.Length == 0)
        {
            SecondsRemaining = seconds;
            CountdownText = string.Format(CultureInfo.CurrentCulture, Strings.CountdownFormat, seconds);
        }

        if (seconds == 0)
        {
            Complete(IncomingRequestDecision.TimedOut);
        }
    }

    private void Complete(IncomingRequestDecision decision)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => Complete(decision));
            return;
        }

        if (IsCompleted)
        {
            return;
        }

        _timer.Stop();
        Decision = decision;
        IsCompleted = true;
        if (decision == IncomingRequestDecision.TimedOut)
        {
            CountdownText = Strings.RequestExpired;
        }

        _logger.LogInformation("Incoming request {RequestId} completed: {Decision}", RequestId, decision);
        _completion.TrySetResult(decision);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (!IsCompleted)
        {
            Complete(IncomingRequestDecision.Dismissed);
        }
    }
}
