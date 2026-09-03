using RouteBridge.App.Models;

namespace RouteBridge.App.Services;

/// <summary>Shows an incoming request to the host (toast + top-most window with sound) and returns the decision.</summary>
public interface IIncomingRequestPresenter
{
    Task<IncomingRequestDecision> PresentAsync(IncomingRequest request, CancellationToken ct);
}
