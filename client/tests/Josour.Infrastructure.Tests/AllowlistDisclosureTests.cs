using System.Net;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The host's pre-accept disclosure (product document section 15): the sites of the request's OWN allow-list version, and
/// — the point of the whole class — a failure that is visibly a failure instead of an empty list.
/// </summary>
public sealed class AllowlistDisclosureTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static AllowlistDisclosureService Create(FakeApiClient api, TimeSpan? timeout = null) =>
        new(api, logger: null, timeout is null ? null : AllowlistDisclosureOptions.Default with { Timeout = timeout.Value });

    [Fact]
    public async Task AsksForTheRequestsOwnVersion_AndReturnsItsSites()
    {
        var api = new FakeApiClient();

        var disclosure = await Create(api).DescribeAsync(9, None);

        Assert.Equal(9, Assert.Single(api.DomainsVersionCalls));
        Assert.True(disclosure.Loaded);
        Assert.Equal(9, disclosure.Version);
        Assert.Equal(new[] { "example.com", "=exact.com", "portal.corp:8443" }, disclosure.Sites);
        Assert.False(disclosure.IsEmpty);
    }

    [Fact]
    public async Task SecondRequestAtTheSameVersion_IsAnsweredFromTheCache()
    {
        var api = new FakeApiClient();
        var service = Create(api);

        await service.DescribeAsync(9, None);
        var again = await service.DescribeAsync(9, None);

        Assert.Single(api.DomainsVersionCalls);
        Assert.True(again.Loaded);

        // …but a new version is fetched: the version is exactly what makes the disclosure honest.
        await service.DescribeAsync(10, None);
        Assert.Equal(new[] { 9, 10 }, api.DomainsVersionCalls);
    }

    [Fact]
    public async Task ServerUnreachable_IsNotAnEmptyList()
    {
        var api = new FakeApiClient { DomainsVersionError = new ApiUnavailableException("no route to host") };

        var disclosure = await Create(api).DescribeAsync(9, None);

        Assert.False(disclosure.Loaded);
        Assert.Empty(disclosure.Sites);
        Assert.False(disclosure.IsEmpty); // "could not be loaded" must never read as "nothing is allowed"
        Assert.Equal(9, disclosure.Version);
    }

    [Fact]
    public async Task VersionTheServerNoLongerHas_IsReportedAsUnavailable()
    {
        var api = new FakeApiClient
        {
            DomainsVersionError = new ApiException(HttpStatusCode.NotFound, ApiErrorCodes.NotFound, "no such version"),
        };

        var disclosure = await Create(api).DescribeAsync(3, None);

        Assert.False(disclosure.Loaded);
        Assert.Empty(disclosure.Sites);
    }

    [Fact]
    public async Task AFailureIsNotCached_SoTheNextRequestTriesAgain()
    {
        var api = new FakeApiClient { DomainsVersionError = new ApiUnavailableException("down") };
        var service = Create(api);

        Assert.False((await service.DescribeAsync(9, None)).Loaded);

        api.DomainsVersionError = null;
        var second = await service.DescribeAsync(9, None);

        Assert.True(second.Loaded);
        Assert.Equal(new[] { 9, 9 }, api.DomainsVersionCalls);
    }

    [Fact]
    public async Task SlowServer_GivesUpWithinTheDisclosureBudget_NotTheRequestsMinute()
    {
        var api = new FakeApiClient { DomainsVersionDelay = TimeSpan.FromSeconds(30) };

        var started = DateTime.UtcNow;
        var disclosure = await Create(api, TimeSpan.FromMilliseconds(200)).DescribeAsync(9, None);

        Assert.False(disclosure.Loaded);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "the prompt waited far too long for the allow-list");
    }

    [Fact]
    public async Task AVersionTheServerContradicts_IsNotShownAsTheRequestsList()
    {
        // GET /domains?version=9 answering with version 11 would put the wrong sites under the host's decision.
        var api = new FakeApiClient { DomainsVersionOverride = 11 };

        var disclosure = await Create(api).DescribeAsync(9, None);

        Assert.False(disclosure.Loaded);
        Assert.Equal(9, disclosure.Version);
    }

    [Fact]
    public async Task AnEmptyVersion_IsShownAsGenuinelyEmpty()
    {
        var api = new FakeApiClient();
        api.DomainEntries.Clear();

        var disclosure = await Create(api).DescribeAsync(9, None);

        Assert.True(disclosure.Loaded);
        Assert.True(disclosure.IsEmpty);
        Assert.Empty(disclosure.Sites);
    }

    [Fact]
    public async Task AVersionNumberThatCannotExist_NeverReachesTheServer()
    {
        var api = new FakeApiClient();

        var disclosure = await Create(api).DescribeAsync(0, None);

        Assert.Empty(api.DomainsVersionCalls);
        Assert.False(disclosure.Loaded);
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotSwallowedAsAFailedFetch()
    {
        var api = new FakeApiClient { DomainsVersionDelay = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource();
        var pending = Create(api).DescribeAsync(9, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
