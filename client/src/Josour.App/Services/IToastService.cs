namespace Josour.App.Services;

/// <summary>Windows toast notifications (Action Center).</summary>
public interface IToastService
{
    /// <summary>Toast with Accept/Reject buttons whose activation arguments are <c>action=accept|reject;request_id=&lt;guid&gt;</c>.</summary>
    Task ShowIncomingRequestAsync(string guestName, string deviceName, int durationMin, Guid requestId, CancellationToken ct);

    void ShowInfo(string title, string text);

    /// <summary>Removes the incoming-request toast once it was answered from the window (or expired).</summary>
    void ClearIncomingRequest(Guid requestId);
}
