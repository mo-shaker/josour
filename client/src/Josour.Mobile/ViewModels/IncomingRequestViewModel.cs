using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Josour.Core.Control;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;

namespace Josour.Mobile.ViewModels;

public enum RequestDecision { Accepted, Rejected, TimedOut, Dismissed }

public sealed record RequestAnswer(RequestDecision Decision, bool AlwaysAllow);

/// <summary>
/// The phone's consent screen for one <c>request.incoming</c>: the same disclosure the desktop window shows (who, which device,
/// how long, the scope of what the guest may reach, the privacy notice), from the same strings, with the countdown taken from
/// the server's clock. It completes once — accepted, rejected, timed out or dismissed.
/// </summary>
public sealed partial class IncomingRequestViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly RequestCountdown _countdown;
    private readonly TaskCompletionSource<RequestAnswer> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _initialSeconds;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(RejectCommand))]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _trustThisGuest;

    /// <summary>The time left to answer, from 1 down to 0, for the bar under the countdown.</summary>
    [ObservableProperty]
    private double _countdownFraction = 1;

    /// <summary>It ran out before an answer: the screen says so until it is closed (<see cref="Closed"/>).</summary>
    [ObservableProperty]
    private bool _isExpired;

    public IncomingRequestViewModel(RequestIncomingMessage request, AllowlistDisclosure allowlist, Func<DateTimeOffset> serverNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowlist);
        RequestId = request.RequestId;
        GuestUserId = request.GuestUserId;
        GuestName = request.GuestName;
        _countdown = new RequestCountdown(request.ExpiresAt, serverNow);

        Heading = Tr.Format(UiStringKeys.IncomingRequestHeadingFormat, request.GuestName);
        GuestDevice = Tr.Ltr(request.GuestDevice);
        DurationText = Tr.Format(UiStringKeys.DurationMinutesFormat, request.DurationMin);
        ScopeLabel = DisclosureText.Label(allowlist);
        ScopeNote = DisclosureText.Note(allowlist);
        // The phone says the unrestricted scope in plainer words, with the same breadth (MobileScopeUnrestricted).
        SitesMessage = allowlist.Unrestricted ? Tr.Get(UiStringKeys.MobileScopeUnrestricted) : DisclosureText.Message(allowlist);
        SitesMessageIsWarning = !allowlist.Unrestricted && (!allowlist.Loaded || allowlist.IsEmpty);
        Sites = allowlist.Sites.Select(Tr.Ltr).ToArray();
        CanTrust = request.GuestUserId != Guid.Empty;
        ExpiredText = Tr.Format(UiStringKeys.MobileRequestExpiredFormat, request.GuestName);
        _initialSeconds = Math.Max(1, _countdown.SecondsRemaining());

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
    }

    public Guid RequestId { get; }

    public Guid GuestUserId { get; }

    public string GuestName { get; }

    public string Heading { get; }

    public string GuestDevice { get; }

    public string DurationText { get; }

    public string ScopeLabel { get; }

    public string ScopeNote { get; }

    public bool HasScopeNote => ScopeNote.Length > 0;

    public string SitesMessage { get; }

    public bool HasSitesMessage => SitesMessage.Length > 0;

    /// <summary>The list could not be loaded, or allows nothing: said in a warning, never as a plain sentence.</summary>
    public bool SitesMessageIsWarning { get; }

    public bool SitesMessageIsPlain => HasSitesMessage && !SitesMessageIsWarning;

    public string ExpiredText { get; }

    /// <summary>Completes when an expired request is closed, by the user or because the screen is torn down.</summary>
    public Task Closed => _closed.Task;

    public IReadOnlyList<string> Sites { get; }

    public bool HasSites => Sites.Count > 0;

    /// <summary>"Always let this contact connect" needs a server that names the guest's account.</summary>
    public bool CanTrust { get; }

    public Task<RequestAnswer> Completion => _completion.Task;

    private bool CanDecide() => !IsCompleted;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Accept() => Complete(RequestDecision.Accepted);

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject() => Complete(RequestDecision.Rejected);

    public void Expire() => Complete(RequestDecision.TimedOut);

    [RelayCommand]
    public void Close() => _closed.TrySetResult();

    private void Tick()
    {
        var seconds = _countdown.SecondsRemaining();
        CountdownText = Tr.Format(UiStringKeys.CountdownFormat, seconds);
        CountdownFraction = Math.Clamp(seconds / (double)_initialSeconds, 0, 1);
        if (_countdown.HasRunOut())
        {
            Complete(RequestDecision.TimedOut);
        }
    }

    private void Complete(RequestDecision decision)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Complete(decision));
            return;
        }

        if (IsCompleted)
        {
            return;
        }

        _timer.Stop();
        IsCompleted = true;
        if (decision == RequestDecision.TimedOut)
        {
            CountdownText = Tr.Get(UiStringKeys.RequestExpired);
            CountdownFraction = 0;
            IsExpired = true;
        }

        // Trust only ever rides along with an acceptance.
        _completion.TrySetResult(new RequestAnswer(decision, decision == RequestDecision.Accepted && TrustThisGuest && CanTrust));
    }

    public void Dispose()
    {
        _timer.Stop();
        Complete(RequestDecision.Dismissed);
        _closed.TrySetResult();
    }
}
