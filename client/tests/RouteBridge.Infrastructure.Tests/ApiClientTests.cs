using System.Net;
using System.Text.Json;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

public sealed class ApiClientTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static (ApiClient Api, FakeHttpMessageHandler Http, InMemoryAppSettingsStore Settings) Create(string serverUrl = "https://server.test")
    {
        var http = new FakeHttpMessageHandler();
        var settings = new InMemoryAppSettingsStore(serverUrl);
        var api = new ApiClient(ApiClient.CreateHttpClient(http, "RouteBridge-tests/0.2.0"), settings);
        return (api, http, settings);
    }

    [Fact]
    public async Task Login_PostsContractBody_Anonymously_AndParsesResponse()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-1", "rt-1", deviceSecret: "dev-secret-1")));

        var response = await api.LoginAsync(" guest@example.com ", "pw", new DeviceLoginInfo(null, null, "TEST-PC", "Windows 11 Pro", "22631"), None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("/api/v1/auth/login", request.Uri.AbsolutePath);
        Assert.Null(request.Authorization);
        Assert.Contains("RouteBridge-tests/0.2.0", request.UserAgent);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("guest@example.com", root.GetProperty("email").GetString());
        Assert.Equal("pw", root.GetProperty("password").GetString());
        var device = root.GetProperty("device");
        Assert.False(device.TryGetProperty("id", out _));      // first login: no id/secret on the wire at all
        Assert.False(device.TryGetProperty("secret", out _));
        Assert.Equal("TEST-PC", device.GetProperty("name").GetString());
        Assert.Equal("Windows 11 Pro", device.GetProperty("os_version").GetString());
        Assert.Equal("22631", device.GetProperty("os_build").GetString());

        Assert.Equal("at-1", response.AccessToken);
        Assert.Equal("rt-1", response.RefreshToken);
        Assert.Equal(900, response.ExpiresIn);
        Assert.Equal(AuthHarness.UserId, response.User.Id);
        Assert.Equal("Guest", response.User.DisplayName);
        Assert.Equal(AuthHarness.DeviceId, response.Device.Id);
        Assert.Equal("dev-secret-1", response.Device.Secret);
    }

    [Fact]
    public async Task ErrorEnvelope_IsParsedIntoApiException()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Locked, "account_locked", "Locked for 15 minutes"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync("a@b.c", "pw", new DeviceLoginInfo(null, null, "PC", "Win", null), None));

        Assert.Equal(HttpStatusCode.Locked, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountLocked, ex.Code);
        Assert.Equal("Locked for 15 minutes", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", ApiErrorCodes.Internal)]
    [InlineData(HttpStatusCode.TooManyRequests, "", ApiErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":\"nope\"}", ApiErrorCodes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "", ApiErrorCodes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, "", ApiErrorCodes.NotFound)]
    public async Task ErrorWithoutEnvelope_FallsBackToStatusCode(HttpStatusCode status, string body, string expectedCode)
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, AuthHarness.MePath, _ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetMeAsync(None));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsApiUnavailable()
    {
        var (api, http, _) = Create();
        http.Throw = new HttpRequestException("No such host is known");

        var ex = await Assert.ThrowsAsync<ApiUnavailableException>(() => api.GetMeAsync(None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Timeout_ThrowsApiUnavailable_NotOperationCanceled()
    {
        var http = new FakeHttpMessageHandler();
        http.On(HttpMethod.Get, AuthHarness.MePath, async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson);
        });
        var client = ApiClient.CreateHttpClient(http);
        client.Timeout = TimeSpan.FromMilliseconds(150);
        var api = new ApiClient(client, new InMemoryAppSettingsStore());

        await Assert.ThrowsAsync<ApiUnavailableException>(() => api.GetMeAsync(None));
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        var http = new FakeHttpMessageHandler();
        http.On(HttpMethod.Get, AuthHarness.MePath, async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson);
        });
        var api = new ApiClient(ApiClient.CreateHttpClient(http), new InMemoryAppSettingsStore());
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetMeAsync(cts.Token));
    }

    [Fact]
    public async Task GetDomains_SendsIfNoneMatch_And304IsNotModified()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, "/api/v1/domains", request =>
        {
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"3\""))
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"3\"");
                return notModified;
            }

            var ok = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"version":3,"entries":["example.com","=exact.com","portal.corp:8443"]}""");
            ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"3\"");
            return ok;
        });

        var first = await api.GetDomainsAsync(knownVersion: null, None);
        var second = await api.GetDomainsAsync(knownVersion: 3, None);
        var stale = await api.GetDomainsAsync(knownVersion: 2, None);

        Assert.False(first.IsNotModified);
        Assert.Equal(3, first.Domains!.Version);
        Assert.Equal(new[] { "example.com", "=exact.com", "portal.corp:8443" }, first.Domains.Entries);
        Assert.Equal("\"3\"", first.ETag);

        Assert.True(second.IsNotModified);
        Assert.Same(DomainsResult.NotModified, second);
        Assert.Null(second.Domains);

        Assert.False(stale.IsNotModified);

        Assert.Null(http.Requests[0].IfNoneMatch);
        Assert.Equal("\"3\"", http.Requests[1].IfNoneMatch);
        Assert.Equal("\"2\"", http.Requests[2].IfNoneMatch);
    }

    [Fact]
    public async Task GetDomainsVersion_AsksForThatVersion_AndSurfacesAMissingOne()
    {
        // The host's pre-accept disclosure needs the version the request names, not today's list (docs/api.md).
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, "/api/v1/domains", request =>
            request.RequestUri!.Query.Contains("version=3", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"version":3,"entries":["example.com","portal.corp:8443"]}""")
                : FakeHttpMessageHandler.Error(HttpStatusCode.NotFound, "not_found", "no such allow-list version"));

        var domains = await api.GetDomainsVersionAsync(3, None);

        Assert.Equal(3, domains.Version);
        Assert.Equal(new[] { "example.com", "portal.corp:8443" }, domains.Entries);
        Assert.Equal("?version=3", http.Requests[0].Uri.Query);
        Assert.Equal("/api/v1/domains", http.Requests[0].Uri.AbsolutePath);

        var missing = await Assert.ThrowsAsync<ApiException>(() => api.GetDomainsVersionAsync(99, None));
        Assert.Equal(ApiErrorCodes.NotFound, missing.Code);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.GetDomainsVersionAsync(0, None));
    }

    [Fact]
    public async Task GetHosts_ParsesSnapshotShape_IncludingNullReachable()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, "/api/v1/hosts", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            [{"device_id":"11111111-1111-4111-8111-111111111111","user_display_name":"Omar","device_name":"OMAR-DESKTOP","reachable":true},
             {"device_id":"22222222-2222-4222-8222-222222222222","user_display_name":"Lina","device_name":"LINA-LAPTOP","reachable":null}]
            """));

        var hosts = await api.GetHostsAsync(None);

        Assert.Equal(2, hosts.Count);
        Assert.Equal(new Guid("11111111-1111-4111-8111-111111111111"), hosts[0].DeviceId);
        Assert.True(hosts[0].Reachable);
        Assert.Equal("LINA-LAPTOP", hosts[1].DeviceName);
        Assert.Null(hosts[1].Reachable);
    }

    [Fact]
    public async Task Probe_PostsIpAndPort_AndParsesNullLatency()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Post, "/api/v1/probe", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"reachable":false,"latency_ms":null}"""));

        var result = await api.ProbeAsync("203.0.113.10", 45000, None);

        Assert.False(result.Reachable);
        Assert.Null(result.LatencyMs);
        Assert.Equal("""{"ip":"203.0.113.10","port":45000}""", http.Requests.Single().Body);
    }

    [Fact]
    public async Task GetMySessions_PassesLimit_AndParsesTimes()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, "/api/v1/sessions/me", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            [{"id":"cccccccc-0000-4000-8000-000000000003","role":"guest","peer_display_name":"Omar","peer_device_name":"OMAR-DESKTOP","status":"ended",
              "created_at":"2026-09-04T10:00:00.000Z","started_at":"2026-09-04T10:00:05.000Z","ended_at":null,"end_reason":"guest_ended","bytes_up":10,"bytes_down":20}]
            """));

        var sessions = await api.GetMySessionsAsync(25, None);

        Assert.Equal("limit=25", http.Requests.Single().Uri.Query.TrimStart('?'));
        var session = Assert.Single(sessions);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero), session.CreatedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(20, session.BytesDown);
    }

    [Fact]
    public async Task DeleteMyDevice_And_Logout_Accept204()
    {
        var (api, http, _) = Create();
        var id = Guid.NewGuid();
        http.On(HttpMethod.Delete, $"/api/v1/me/devices/{id:D}", _ => FakeHttpMessageHandler.NoContent());
        http.On(HttpMethod.Post, AuthHarness.LogoutPath, _ => FakeHttpMessageHandler.NoContent());

        await api.DeleteMyDeviceAsync(id, None);
        await api.LogoutAsync("rt-1", None);

        Assert.Equal("""{"refresh_token":"rt-1"}""", http.RequestsTo(HttpMethod.Post, AuthHarness.LogoutPath).Single().Body);
        Assert.Null(http.Requests[1].Authorization);
    }

    [Fact]
    public async Task ServerUrl_Missing_ThrowsApiUnavailable_WithoutSendingAnything()
    {
        var (api, http, _) = Create(serverUrl: string.Empty);

        await Assert.ThrowsAsync<ApiUnavailableException>(() => api.GetMeAsync(None));

        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("https://server.test", "/api/v1/me")]
    [InlineData("https://server.test/", "/api/v1/me")]
    [InlineData("https://server.test/rb", "/rb/api/v1/me")]
    [InlineData("https://server.test/rb/", "/rb/api/v1/me")]
    [InlineData("http://localhost:8000", "/api/v1/me")]
    public async Task ServerUrl_PrefixAndTrailingSlash_AreRespected(string serverUrl, string expectedPath)
    {
        var (api, http, _) = Create(serverUrl);
        http.On(HttpMethod.Get, expectedPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson));

        var me = await api.GetMeAsync(None);

        Assert.Equal("guest@example.com", me.Email);
        Assert.Equal(expectedPath, http.Requests.Single().Uri.AbsolutePath);
    }

    [Fact]
    public async Task MalformedSuccessBody_ThrowsInvalidResponse()
    {
        var (api, http, _) = Create();
        http.On(HttpMethod.Get, AuthHarness.MePath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "<html>captive portal</html>"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetMeAsync(None));

        Assert.Equal(ApiErrorCodes.InvalidResponse, ex.Code);
    }

    [Theory]
    [InlineData("https://server.test", true, "https://server.test/")]
    [InlineData("  https://Server.test/rb/  ", true, "https://server.test/rb/")]
    [InlineData("http://localhost:8000", true, "http://localhost:8000/")]
    [InlineData("http://127.0.0.1:8000/", true, "http://127.0.0.1:8000/")]
    [InlineData("http://server.test", false, "http://server.test/")]
    public void ServerUrl_Normalize_AndSecurityRule(string input, bool allowed, string normalized)
    {
        Assert.True(ServerUrl.TryNormalize(input, out var uri));
        Assert.Equal(normalized, uri.ToString());
        Assert.Equal(allowed, ServerUrl.IsAllowed(uri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("server.test")]
    [InlineData("ftp://server.test")]
    [InlineData("https://server.test/?x=1")]
    [InlineData("https://server.test/#frag")]
    public void ServerUrl_Invalid_IsRejected(string input)
    {
        Assert.False(ServerUrl.TryNormalize(input, out _));
    }
}
