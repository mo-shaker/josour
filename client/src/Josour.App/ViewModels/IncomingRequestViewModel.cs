using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Models;
using Josour.Infrastructure.Session;

namespace Josour.App.ViewModels;

/// <summary>
/// Drives <see cref="Views.IncomingRequestWindow"/>: the pre-accept disclosure of the product document section 15
/// (requester, device, requested duration, the sites that request's allow-list version permits, the public-IP notice and
/// the reminder that the host can disconnect at any time), the 60 s countdown from the request's <c>expires_at</c>, and
/// Accept/Reject.
/// <para>
/// Create it on the UI thread — it owns a <see cref="DispatcherTimer"/>; decisions arriving from other threads (toast
/// buttons) are marshalled back to that thread.
/// </para>
/// </summary>
public sealed partial class IncomingRequestViewModel : ObservableObject, IDisposable
{
    private readonly IncomingRequest _request;
    private readonly ILogger<IncomingRequestViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly RequestCountdown _countdown;
    private readonly TaskCompletionSource<IncomingRequestDecision> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(RejectCommand))]
    private bool _isCompleted;

    /// <param name="serverNow">
    /// The server's clock as the app knows it. Defaults to the local clock only so that the debug
    /// simulator can build one without a session; the real path always supplies the corrected clock,
    /// because <c>expires_at</c> is the server's and a fast local clock used to kill the window.
    /// </param>
    public IncomingRequestViewModel(
        IncomingRequest request,
        ILogger<IncomingRequestViewModel> logger,
        Func<DateTimeOffset>? serverNow = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _countdown = new RequestCountdown(request.ExpiresAt, serverNow ?? (() => DateTimeOffset.UtcNow));

        Heading = string.Format(UiFlow.Culture, Strings.IncomingRequestHeadingFormat, request.GuestName);
        DurationText = string.Format(UiFlow.Culture, Strings.DurationMinutesFormat, request.DurationMinutes);

        // Device names and allow-list entries are technical values: keep them left to right inside the Arabic window.
        GuestDevice = UiFlow.Ltr(request.GuestDevice);
        AllowedSites = request.Allowlist.Sites.Select(UiFlow.Ltr).ToArray();
        AllowedSitesSummary = request.Allowlist.Loaded
            ? string.Format(UiFlow.Culture, Strings.AllowedSitesSummaryFormat, request.Allowlist.Sites.Count, request.Allowlist.Version)
            : string.Empty;
        AllowedSitesMessage = DescribeAllowlist(request.Allowlist);

        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
    }

    public Guid RequestId => _request.RequestId;

    public string GuestName => _request.GuestName;

    /// <summary>The requester's device name, kept left to right.</summary>
    public string GuestDevice { get; }

    public int DurationMinutes => _request.DurationMinutes;

    public DateTimeOffset ExpiresAt => _request.ExpiresAt;

    public string Heading { get; }

    public string DurationText { get; }

    /// <summary>The sites that this request's allow-list version permits; empty when it could not be loaded.</summary>
    public IReadOnlyList<string> AllowedSites { get; }

    public bool HasAllowedSites => AllowedSites.Count > 0;

    /// <summary>"N entries · allow-list version V"; empty when the list could not be loaded.</summary>
    public string AllowedSitesSummary { get; }

    /// <summary>
    /// Why there is no list to look at: "could not be loaded" or "this version allows nothing". Empty when the list is
    /// shown. The two are deliberately different sentences — an empty list would otherwise read as a harmless request.
    /// </summary>
    public string AllowedSitesMessage { get; }

    public bool HasAllowedSitesMessage => AllowedSitesMessage.Length > 0;

    /// <summary>True when the allow-list could not be fetched at all (the window says so, in a warning).</summary>
    public bool IsAllowlistUnavailable => !_request.Allowlist.Loaded;

    public IncomingRequestDecision? Decision { get; private set; }

    /// <summary>Completes with the decision (Accept/Reject/TimedOut/Dismissed).</summary>
    public Task<IncomingRequestDecision> Completion => _completion.Task;

    /// <summary>Raised on the UI thread once a decision exists; the window closes itself on it.</summary>
    public event EventHandler? Completed;

    /// <summary>The disclosure's failure sentence, or empty when the list itself is on screen.</summary>
    public static string DescribeAllowlist(AllowlistDisclosure allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        if (allowlist.Unrestricted)
        {
            // Not "no list" and not "an empty list": no restriction. The host is agreeing to something
            // wider than a named set, and the window must say so before the answer, not after.
            return Strings.AllowedSitesUnrestricted;
        }

        if (!allowlist.Loaded)
        {
            return Strings.AllowedSitesUnavailable;
        }

        return allowlist.IsEmpty ? Strings.AllowedSitesEmpty : string.Empty;
    }

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
        var seconds = _countdown.SecondsRemaining();
        if (seconds != SecondsRemaining || CountdownText.Length == 0)
        {
            SecondsRemaining = seconds;
            CountdownText = string.Format(UiFlow.Culture, Strings.CountdownFormat, seconds);
        }

        // Only a window that was genuinely open can run out. A request that reads as expired the moment
        // it arrives is a clock, not a fact, and the server sends request.expired when it truly is one.
        if (_countdown.HasRunOut())
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
