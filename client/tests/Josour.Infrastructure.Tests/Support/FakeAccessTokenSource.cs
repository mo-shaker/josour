using Josour.Infrastructure.Api;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>An <see cref="IAccessTokenSource"/> the test drives by hand: how many refreshes happened and what they return.</summary>
public sealed class FakeAccessTokenSource : IAccessTokenSource
{
    public FakeAccessTokenSource(string? accessToken = "at-1")
    {
        AccessToken = accessToken;
    }

    public string? AccessToken { get; set; }

    /// <summary>What <see cref="RefreshAccessTokenAsync"/> returns; null means "the session was signed out".</summary>
    public string? RefreshedToken { get; set; } = "at-2";

    public int RefreshCount { get; private set; }

    public List<string?> RejectedTokens { get; } = new();

    public Task<string?> RefreshAccessTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        RefreshCount++;
        RejectedTokens.Add(rejectedToken);
        AccessToken = RefreshedToken;
        return Task.FromResult(RefreshedToken);
    }
}
