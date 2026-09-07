using System.Net;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The server address the user types: validated the same way the API client will validate it later, then asked whether it
/// is alive through the one unauthenticated endpoint (<c>GET /healthz</c>), because the check has to work before anyone has
/// signed in — which is exactly when it is needed.
/// </summary>
public sealed class ServerCheckTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static (HttpServerCheck Check, FakeHttpMessageHandler Http) Create(TimeSpan? timeout = null)
    {
        var http = new FakeHttpMessageHandler();
        return (new HttpServerCheck(http, timeout ?? TimeSpan.FromSeconds(2)), http);
    }

    private static void Healthy(FakeHttpMessageHandler http) =>
        http.On(HttpMethod.Get, "/healthz", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""));

    // ---- validation, without touching the network ----

    [Theory]
    [InlineData("https://josour.example.com", "https://josour.example.com")]
    [InlineData("  https://Josour.Example.com/  ", "https://josour.example.com")]
    [InlineData("https://josour.example.com/rb", "https://josour.example.com/rb")]
    [InlineData("http://localhost:8000", "http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000/", "http://127.0.0.1:8000")]
    public void AUsableAddress_IsNormalizedWithoutATrailingSlash(string input, string expected)
    {
        var result = HttpServerCheck.Validate(input);

        Assert.True(result.IsOk);
        Assert.Equal(expected, result.NormalizedUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("josour.example.com")]
    [InlineData("ftp://josour.example.com")]
    [InlineData("https://josour.example.com/?x=1")]
    [InlineData("https://josour.example.com/#frag")]
    public void AnUnusableAddress_IsInvalid(string? input)
    {
        var result = HttpServerCheck.Validate(input);

        Assert.Equal(ServerCheckStatus.InvalidUrl, result.Status);
        Assert.Null(result.NormalizedUrl);
    }

    [Fact]
    public void PlainHttpToARemoteHost_IsRefusedAsInsecure_NotMerelyInvalid()
    {
        // The two are different sentences to the user: one is a typo, the other is a security rule.
        var result = HttpServerCheck.Validate("http://josour.example.com");

        Assert.Equal(ServerCheckStatus.InsecureScheme, result.Status);
        Assert.Equal("http", result.Detail);
    }

    // ---- the reachability check ----

    [Fact]
    public async Task AHealthyServer_IsOk_AndTheCheckHitsHealthz()
    {
        var (check, http) = Create();
        Healthy(http);

        var result = await check.CheckAsync("https://josour.example.com", None);

        Assert.True(result.IsOk);
        Assert.Equal("https://josour.example.com", result.NormalizedUrl);
        Assert.Equal("/healthz", Assert.Single(http.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task APathPrefix_IsKept_SoAServerBehindAReverseProxyStillAnswers()
    {
        var (check, http) = Create();
        http.On(HttpMethod.Get, "/rb/healthz", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""));

        var result = await check.CheckAsync("https://josour.example.com/rb", None);

        Assert.True(result.IsOk);
        Assert.Equal("/rb/healthz", Assert.Single(http.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task AnUnreachableServer_IsUnreachable_AndSaysWhy()
    {
        var (check, http) = Create();
        http.Throw = new HttpRequestException("No such host is known.");

        var result = await check.CheckAsync("https://josour.example.com", None);

        Assert.Equal(ServerCheckStatus.Unreachable, result.Status);
        Assert.Contains("No such host", result.Detail);
    }

    [Fact]
    public async Task AServerThatAnswersSomethingElse_IsNotJosour()
    {
        // A captive portal or the wrong host: "not reachable" would send the user hunting the wrong problem.
        var (check, http) = Create();
        http.On(HttpMethod.Get, "/healthz", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in to the hotel Wi-Fi</html>") });

        var result = await check.CheckAsync("https://josour.example.com", None);

        Assert.Equal(ServerCheckStatus.NotJosour, result.Status);
    }

    [Fact]
    public async Task AnHttpErrorStatus_IsAlsoNotJosour()
    {
        var (check, http) = Create();
        http.On(HttpMethod.Get, "/healthz", _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await check.CheckAsync("https://josour.example.com", None);

        Assert.Equal(ServerCheckStatus.NotJosour, result.Status);
        Assert.Equal("HTTP 502", result.Detail);
    }

    [Fact]
    public async Task AnInvalidAddress_IsNeverPutOnTheNetwork()
    {
        var (check, http) = Create();
        Healthy(http);

        var result = await check.CheckAsync("not a url", None);

        Assert.Equal(ServerCheckStatus.InvalidUrl, result.Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task AServerThatNeverAnswers_TimesOutOnItsOwnShortBudget()
    {
        var (check, http) = Create(TimeSpan.FromMilliseconds(200));
        http.On(HttpMethod.Get, "/healthz", async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}""");
        });

        var result = await check.CheckAsync("https://josour.example.com", None);

        Assert.Equal(ServerCheckStatus.Unreachable, result.Status);
        Assert.Contains("timeout", result.Detail);
    }

    [Fact]
    public async Task TheCallersOwnCancellation_IsNotSwallowedAsAnUnreachableServer()
    {
        var (check, http) = Create(TimeSpan.FromSeconds(30));
        http.On(HttpMethod.Get, "/healthz", async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}""");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckAsync("https://josour.example.com", cts.Token));
    }
}
