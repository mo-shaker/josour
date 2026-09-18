namespace Josour.App.Services.Notifications;

/// <summary>
/// Desktop notifications: how the app asks for the host's attention, never how it takes the host's answer.
/// <para>
/// That distinction is the whole reason this is one interface with three unequal implementations. Windows can put
/// Accept and Reject on a toast; macOS, for an app that is neither signed nor bundled, can only show a banner with no
/// buttons at all; Linux has nothing here yet. So the request WINDOW is the path that must always work on every
/// platform, and a notification that failed to appear is a worse experience, not a broken flow.
/// </para>
/// <para>Nothing here throws: a machine with notifications switched off must still be able to accept a request.</para>
/// </summary>
public interface INotifier
{
    /// <summary>
    /// Called once at start-up, before anything is shown. On Windows this is what makes a notification button reach
    /// this process; elsewhere it is a no-op.
    /// </summary>
    void RegisterActivation();

    /// <summary>
    /// Announces an incoming request. On Windows it carries Accept/Reject whose activation arguments are
    /// <c>action=accept|reject;request_id=&lt;guid&gt;</c>; elsewhere it only points at the window.
    /// </summary>
    Task ShowIncomingRequestAsync(string guestName, string deviceName, int durationMin, Guid requestId, CancellationToken ct);

    /// <summary>A one-way message, such as "a trusted guest connected" (docs/ws-protocol.md section 5a).</summary>
    void ShowInfo(string title, string text);

    /// <summary>Withdraws the incoming-request notification once it was answered from the window (or expired).</summary>
    void ClearIncomingRequest(Guid requestId);
}
