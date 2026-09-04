using RouteBridge.App.Models;

namespace RouteBridge.App.Services;

/// <summary>Shows an incoming request to the host (toast + top-most window with sound) and returns the decision.</summary>
public interface IIncomingRequestPresenter
{
    Task<IncomingRequestDecision> PresentAsync(IncomingRequest request, CancellationToken ct);

    /// <summary>The server sent <c>request.expired</c>: close the prompt for that request (no-op when it is not being shown).</summary>
    void Expire(Guid requestId);
}
