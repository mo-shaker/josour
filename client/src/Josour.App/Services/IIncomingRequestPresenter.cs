using Josour.App.Models;

namespace Josour.App.Services;

/// <summary>Shows an incoming request to the host (toast + top-most window with sound) and returns the decision.</summary>
public interface IIncomingRequestPresenter
{
    /// <summary>The host's decision, plus any standing rule it wrote while deciding (docs/ws-protocol.md section 5a).</summary>
    Task<IncomingRequestAnswer> PresentAsync(IncomingRequest request, CancellationToken ct);

    /// <summary>The server sent <c>request.expired</c>: close the prompt for that request (no-op when it is not being shown).</summary>
    void Expire(Guid requestId);
}
