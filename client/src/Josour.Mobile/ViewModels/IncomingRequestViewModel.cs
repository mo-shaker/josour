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

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(RejectCommand))]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _trustThisGuest;

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
        SitesMessage = DisclosureText.Message(allowlist);
        Sites = allowlist.Sites.Select(Tr.Ltr).ToArray();
        CanTrust = request.GuestUserId != Guid.Empty;

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

    private void Tick()
    {
        CountdownText = Tr.Format(UiStringKeys.CountdownFormat, _countdown.SecondsRemaining());
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
        }

        // Trust only ever rides along with an acceptance.
        _completion.TrySetResult(new RequestAnswer(decision, decision == RequestDecision.Accepted && TrustThisGuest && CanTrust));
    }

    public void Dispose()
    {
        _timer.Stop();
        Complete(RequestDecision.Dismissed);
    }
}
