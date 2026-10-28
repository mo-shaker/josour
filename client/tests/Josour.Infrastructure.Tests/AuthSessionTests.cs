using System.Net;
using System.Text.Json;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

public sealed class AuthSessionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task SignIn_FirstDevice_SendsNoIdentity_StoresSecretDeviceIdAndRefreshToken()
    {
        using var h = new AuthHarness();

        await h.SignInFreshAsync();

        var login = Assert.Single(h.Http.RequestsTo(HttpMethod.Post, AuthHarness.LoginPath));
        using var body = JsonDocument.Parse(login.Body!);
        var device = body.RootElement.GetProperty("device");
        Assert.False(device.TryGetProperty("id", out _));
        Assert.False(device.TryGetProperty("secret", out _));
        Assert.Equal("TEST-PC", device.GetProperty("name").GetString());
        Assert.Equal("22631.3593", device.GetProperty("os_build").GetString());

        Assert.Equal("rt-1", h.Secrets.Values[AuthSession.RefreshTokenKey]);
        Assert.Equal(AuthHarness.DeviceId.ToString("D"), h.Secrets.Values[AuthSession.DeviceIdKey]);
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);

        Assert.True(h.Session.IsSignedIn);
        Assert.Equal("at-1", h.Session.AccessToken);
        Assert.Equal("guest@example.com", h.Session.CurrentUser!.Email);
        Assert.Equal(AuthHarness.DeviceId, h.Session.DeviceId);
        Assert.Equal(h.Time.Now.AddSeconds(900), h.Session.AccessTokenExpiresAt);
        Assert.Equal(1, h.ChangedCount);
    }

    [Fact]
    public async Task SignIn_KnownDevice_SendsStoredIdAndSecret_StoresNoNewIdentity()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "dev-secret-1";
        h.Http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-2", "rt-2", AuthHarness.DeviceId, deviceSecret: null)));

        await h.Session.SignInAsync("guest@example.com", "Guest-pass-1234", None);

        using var body = JsonDocument.Parse(h.Http.Requests.Single().Body!);
        var device = body.RootElement.GetProperty("device");
        Assert.Equal(AuthHarness.DeviceId, device.GetProperty("id").GetGuid());
        Assert.Equal("dev-secret-1", device.GetProperty("secret").GetString());

        Assert.Equal(0, h.Secrets.SetCount(AuthSession.DeviceSecretKey));
        Assert.Equal(0, h.Secrets.SetCount(AuthSession.DeviceIdKey));
        Assert.Equal(1, h.Secrets.SetCount(AuthSession.RefreshTokenKey));
        Assert.Equal("rt-2", h.Secrets.Values[AuthSession.RefreshTokenKey]);
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);
        Assert.Equal("at-2", h.Session.AccessToken);
    }

    [Fact]
    public async Task SignIn_StoredIdWithoutSecret_RegistersAsNewDevice()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D"); // secret missing → inconsistent identity

        await h.SignInFreshAsync();

        using var body = JsonDocument.Parse(h.Http.Requests.Single().Body!);
        Assert.False(body.RootElement.GetProperty("device").TryGetProperty("id", out _));
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);
    }

    [Fact]
    public async Task AuthenticatedCall_CarriesBearer_AnonymousCallsDoNot()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Get, AuthHarness.MePath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson));

        await h.Api.GetMeAsync(None);

        Assert.Null(h.Http.RequestsTo(HttpMethod.Post, AuthHarness.LoginPath).Single().Authorization);
        Assert.Equal("at-1", h.Http.RequestsTo(HttpMethod.Get, AuthHarness.MePath).Single().Authorization);
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnce_AndRetriesOnce()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Get, AuthHarness.MePath, (request, _) =>
            request.Headers.Authorization?.Parameter == "at-2"
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson)
                : FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "token expired"));
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-2", "rt-2")));

        var me = await h.Api.GetMeAsync(None);

        Assert.Equal("Guest", me.DisplayName);
        var meCalls = h.Http.RequestsTo(HttpMethod.Get, AuthHarness.MePath);
        Assert.Equal(2, meCalls.Count);
        Assert.Equal("at-1", meCalls[0].Authorization);
        Assert.Equal("at-2", meCalls[1].Authorization);

        var refresh = Assert.Single(h.Http.RequestsTo(HttpMethod.Post, AuthHarness.RefreshPath));
        Assert.Equal("""{"refresh_token":"rt-1"}""", refresh.Body);
        Assert.Null(refresh.Authorization);

        Assert.Equal("at-2", h.Session.AccessToken);
        Assert.Equal("rt-2", h.Secrets.Values[AuthSession.RefreshTokenKey]);
        Assert.True(h.Session.IsSignedIn);
        Assert.Empty(h.SignedOutReasons);
    }

    [Fact]
    public async Task ConcurrentUnauthorized_RefreshesOnlyOnce()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Get, AuthHarness.MePath, (request, _) =>
            request.Headers.Authorization?.Parameter == "at-2"
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.MeJson)
                : FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "token expired"));
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, async (_, _) =>
        {
            await Task.Delay(50);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-2", "rt-2"));
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => h.Api.GetMeAsync(None)));

        Assert.All(results, me => Assert.Equal("Guest", me.DisplayName));
        Assert.Equal(1, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
        Assert.Equal(8, h.Http.CallCount(HttpMethod.Get, AuthHarness.MePath)); // 4 × (401 + retry)
    }

    [Fact]
    public async Task RefreshFailure_SignsOut_ClearsRefreshToken_KeepsDeviceIdentity()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Get, AuthHarness.MePath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "token expired"));
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "refresh reused"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Api.GetMeAsync(None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(1, h.Http.CallCount(HttpMethod.Get, AuthHarness.MePath)); // no retry after a failed refresh
        Assert.Equal(1, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));

        Assert.False(h.Session.IsSignedIn);
        Assert.Null(h.Session.AccessToken);
        Assert.Null(h.Session.CurrentUser);
        Assert.Equal(new[] { SignOutReason.SessionExpired }, h.SignedOutReasons);
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.RefreshTokenKey));
        Assert.True(h.Secrets.Values.ContainsKey(AuthSession.DeviceIdKey));
        Assert.True(h.Secrets.Values.ContainsKey(AuthSession.DeviceSecretKey));
    }

    [Fact]
    public async Task RefreshDuringOutage_KeepsSession_AndSurfacesUnavailable()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Get, AuthHarness.MePath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "token expired"));
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<ApiUnavailableException>(() => h.Api.GetMeAsync(None));

        Assert.True(h.Session.IsSignedIn);
        Assert.Equal("rt-1", h.Secrets.Values[AuthSession.RefreshTokenKey]);
        Assert.Empty(h.SignedOutReasons);
    }

    [Fact]
    public async Task SignIn_DeviceUnknownToTheServer_SignsInAgainAsANewDevice_InOneAttempt()
    {
        // A reinstalled server knows none of the old devices. The password is right, so the user must not have to fail once
        // first: the stale identity is dropped and the same sign-in registers a new device.
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.DeviceIdKey] = Guid.NewGuid().ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "stale-secret";
        var logins = 0;
        h.Http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => ++logins == 1
            ? FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "Unknown device or invalid device secret")
            : FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-3", "rt-3", AuthHarness.DeviceId, deviceSecret: "dev-secret-new")));

        await h.Session.SignInAsync("guest@example.com", "Guest-pass-1234", None);

        var sent = h.Http.RequestsTo(HttpMethod.Post, AuthHarness.LoginPath);
        Assert.Equal(2, sent.Count);
        using (var first = JsonDocument.Parse(sent[0].Body!))
        {
            Assert.Equal("stale-secret", first.RootElement.GetProperty("device").GetProperty("secret").GetString());
        }
        using (var second = JsonDocument.Parse(sent[1].Body!))
        {
            Assert.False(second.RootElement.GetProperty("device").TryGetProperty("id", out _));
        }
        Assert.True(h.Session.IsSignedIn);
        Assert.Equal(AuthHarness.DeviceId.ToString("D"), h.Secrets.Values[AuthSession.DeviceIdKey]);
        Assert.Equal("dev-secret-new", h.Secrets.Values[AuthSession.DeviceSecretKey]);
        Assert.Equal("rt-3", h.Secrets.Values[AuthSession.RefreshTokenKey]);
    }

    [Fact]
    public async Task SignIn_DeviceRevoked_SurfacesCode_AndDropsStoredIdentity()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "dev-secret-1";
        h.Http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Forbidden, "device_revoked", "This device was revoked"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Session.SignInAsync("guest@example.com", "pw", None));

        Assert.Equal(ApiErrorCodes.DeviceRevoked, ex.Code);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.False(h.Session.IsSignedIn);
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.DeviceIdKey));
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.DeviceSecretKey));
        Assert.Equal(0, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
    }

    [Fact]
    public async Task SignIn_InvalidCredentials_KeepsIdentity_AndNeverRefreshes()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "dev-secret-1";
        h.Http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "invalid_credentials", "Wrong email or password"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Session.SignInAsync("guest@example.com", "wrong", None));

        Assert.Equal(ApiErrorCodes.InvalidCredentials, ex.Code);
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);
        Assert.Equal(0, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
        Assert.Equal(0, h.ChangedCount);
    }

    [Fact]
    public async Task TryRestore_NoRefreshToken_ReturnsFalse_WithoutRequest()
    {
        using var h = new AuthHarness();

        Assert.False(await h.Session.TryRestoreAsync(None));
        Assert.Empty(h.Http.Requests);
        Assert.False(h.Session.IsSignedIn);
    }

    [Fact]
    public async Task TryRestore_ValidToken_RestoresUser_AndRotatesRefreshToken()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.RefreshTokenKey] = "rt-0";
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-1", "rt-1")));

        Assert.True(await h.Session.TryRestoreAsync(None));

        Assert.Equal("""{"refresh_token":"rt-0"}""", h.Http.Requests.Single().Body);
        Assert.True(h.Session.IsSignedIn);
        Assert.Equal("guest@example.com", h.Session.CurrentUser!.Email);
        Assert.Equal(AuthHarness.DeviceId, h.Session.DeviceId);
        Assert.Equal("rt-1", h.Secrets.Values[AuthSession.RefreshTokenKey]);
        Assert.Equal(1, h.ChangedCount);
    }

    [Fact]
    public async Task TryRestore_Rejected_ClearsRefreshToken_ReturnsFalse()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.RefreshTokenKey] = "rt-0";
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "dev-secret-1";
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "expired"));

        Assert.False(await h.Session.TryRestoreAsync(None));

        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.RefreshTokenKey));
        Assert.True(h.Secrets.Values.ContainsKey(AuthSession.DeviceSecretKey)); // device stays registered
        Assert.False(h.Session.IsSignedIn);
        Assert.Empty(h.SignedOutReasons); // was never signed in during this process
    }

    [Fact]
    public async Task TryRestore_DeviceRevoked_AlsoDropsIdentity()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.RefreshTokenKey] = "rt-0";
        h.Secrets.Values[AuthSession.DeviceIdKey] = AuthHarness.DeviceId.ToString("D");
        h.Secrets.Values[AuthSession.DeviceSecretKey] = "dev-secret-1";
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Error(HttpStatusCode.Forbidden, "device_revoked", "revoked"));

        Assert.False(await h.Session.TryRestoreAsync(None));

        Assert.Empty(h.Secrets.Values);
    }

    [Fact]
    public async Task TryRestore_ServerUnreachable_KeepsToken_ReturnsFalse()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.RefreshTokenKey] = "rt-0";
        h.Http.Throw = new HttpRequestException("offline");

        Assert.False(await h.Session.TryRestoreAsync(None));

        Assert.Equal("rt-0", h.Secrets.Values[AuthSession.RefreshTokenKey]);
    }

    [Fact]
    public async Task SignOut_CallsLogoutWithRefreshToken_ThenClears()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Post, AuthHarness.LogoutPath, _ => FakeHttpMessageHandler.NoContent());

        await h.Session.SignOutAsync(None);

        var logout = Assert.Single(h.Http.RequestsTo(HttpMethod.Post, AuthHarness.LogoutPath));
        Assert.Equal("""{"refresh_token":"rt-1"}""", logout.Body);
        Assert.Null(logout.Authorization);
        Assert.False(h.Session.IsSignedIn);
        Assert.Null(h.Session.DeviceId);
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.RefreshTokenKey));
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);
        Assert.Equal(new[] { SignOutReason.UserRequested }, h.SignedOutReasons);
        Assert.Equal(2, h.ChangedCount);
    }

    [Fact]
    public async Task ForceSignOut_DeviceRevoked_ClearsTheDeviceIdentityWithoutCallingLogout()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();

        await h.Session.ForceSignOutAsync(SignOutReason.DeviceRevoked, None);

        Assert.Empty(h.Http.RequestsTo(HttpMethod.Post, AuthHarness.LogoutPath));
        Assert.False(h.Session.IsSignedIn);
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.RefreshTokenKey));
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.DeviceSecretKey));
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.DeviceIdKey));
        Assert.Equal(new[] { SignOutReason.DeviceRevoked }, h.SignedOutReasons);
    }

    [Fact]
    public async Task ForceSignOut_SessionExpired_KeepsTheDeviceIdentity()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();

        await h.Session.ForceSignOutAsync(SignOutReason.SessionExpired, None);

        Assert.False(h.Session.IsSignedIn);
        Assert.Equal("dev-secret-1", h.Secrets.Values[AuthSession.DeviceSecretKey]);
        Assert.Equal(new[] { SignOutReason.SessionExpired }, h.SignedOutReasons);
    }

    [Fact]
    public async Task SignOut_WhenServerDown_StillClearsLocally()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.Throw = new HttpRequestException("offline");

        await h.Session.SignOutAsync(None);

        Assert.False(h.Session.IsSignedIn);
        Assert.False(h.Secrets.Values.ContainsKey(AuthSession.RefreshTokenKey));
        Assert.Equal(new[] { SignOutReason.UserRequested }, h.SignedOutReasons);
    }

    [Fact]
    public async Task GetValidAccessToken_RefreshesProactively_NearExpiry()
    {
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-2", "rt-2")));

        Assert.Equal("at-1", await h.Session.GetValidAccessTokenAsync(None));
        h.Time.Now = h.Time.Now.AddSeconds(900 - 30);
        Assert.Equal("at-2", await h.Session.GetValidAccessTokenAsync(None));
        Assert.Equal(1, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
    }

    [Fact]
    public async Task SignIn_ClampsDeviceFieldsToServerLimits()
    {
        var http = new FakeHttpMessageHandler();
        http.On(HttpMethod.Post, AuthHarness.LoginPath, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-1", "rt-1", deviceSecret: "s")));
        AuthSession? session = null;
        var api = new ApiClient(ApiClient.CreateHttpClient(new AuthenticatedHandler(() => session!) { InnerHandler = http }), new InMemoryAppSettingsStore());
        session = new AuthSession(api, new InMemorySecretStore(), new LongDeviceInfoProvider());

        await session.SignInAsync("guest@example.com", "pw", None);

        using var body = JsonDocument.Parse(http.Requests.Single().Body!);
        var device = body.RootElement.GetProperty("device");
        Assert.Equal(AuthSession.MaxDeviceNameLength, device.GetProperty("name").GetString()!.Length);
        Assert.Equal(AuthSession.MaxOsVersionLength, device.GetProperty("os_version").GetString()!.Length);
        Assert.Equal(AuthSession.MaxOsBuildLength, device.GetProperty("os_build").GetString()!.Length);
        // Derived from the source rather than pinned by hand: the limit matches the os_version column's length in the database (100).
        var provider = new LongDeviceInfoProvider();
        Assert.Equal(provider.OsVersion[..AuthSession.MaxOsVersionLength], device.GetProperty("os_version").GetString());
        Assert.Equal(provider.DeviceName[..AuthSession.MaxDeviceNameLength], device.GetProperty("name").GetString());
        Assert.Equal(provider.OsBuild![..AuthSession.MaxOsBuildLength], device.GetProperty("os_build").GetString());
    }

    private sealed class LongDeviceInfoProvider : IDeviceInfoProvider
    {
        private readonly DeviceInfo _info = new(
            new string('N', 150),
            "Darwin 25.6.0 Darwin Kernel Version 25.6.0: Mon Aug 11 12:00:00 PDT 2026; root:xnu-12345.678.9~1/RELEASE_ARM64_T8132 arm64 extra",
            new string('B', 80),
            "0.2.0");

        public string DeviceName => _info.DeviceName;

        public string OsVersion => _info.OsVersion;

        public string OsBuild => _info.OsBuild;

        public string AppVersion => _info.AppVersion;

        public DeviceInfo GetDeviceInfo() => _info;
    }

    [Fact]
    public void TokenCarryingRecords_RedactInToString()
    {
        var response = new AuthResponse("ACCESS", "REFRESH", 900, new UserDto(Guid.Empty, "a@b.c", "A", "user"), new AuthDeviceDto(Guid.Empty, "PC", "SECRET"));
        var login = new LoginRequest("a@b.c", "PASSWORD", new DeviceLoginInfo(Guid.Empty, "SECRET", "PC", "Win", null));

        foreach (var text in new[] { response.ToString(), login.ToString(), new RefreshRequest("REFRESH").ToString(), new LogoutRequest("REFRESH").ToString() })
        {
            Assert.DoesNotContain("ACCESS", text);
            Assert.DoesNotContain("REFRESH", text);
            Assert.DoesNotContain("SECRET", text);
            Assert.DoesNotContain("PASSWORD", text);
        }
    }

    [Fact]
    public async Task Refresh_ACallerThatGivesUpMidExchange_StillHasTheRotatedTokenStored()
    {
        // The server has already replaced rt-1 with rt-2 when the caller's budget runs out. Dropping the answer would leave
        // rt-1 on disk, and its next use would make the server revoke the whole chain.
        using var h = new AuthHarness();
        await h.SignInFreshAsync();
        var release = new TaskCompletionSource();
        h.Http.On(HttpMethod.Post, AuthHarness.RefreshPath, async (_, _) =>
        {
            await release.Task;
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthHarness.AuthJson("at-2", "rt-2"));
        });

        using var cts = new CancellationTokenSource();
        var refreshing = h.Session.RefreshAccessTokenAsync("at-1", cts.Token);
        await WaitUntilAsync(() => h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath) == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshing);

        release.SetResult();
        await WaitUntilAsync(() => h.Session.AccessToken == "at-2");
        Assert.Equal("rt-2", h.Secrets.Values[AuthSession.RefreshTokenKey]);

        // The gate was released by the exchange itself: the next caller is not stuck behind the one that left.
        Assert.Equal("at-2", await h.Session.RefreshAccessTokenAsync("at-1", None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
    }

    [Fact]
    public async Task Refresh_WhileSignedOut_DoesNotSpendTheStoredToken()
    {
        using var h = new AuthHarness();
        h.Secrets.Values[AuthSession.RefreshTokenKey] = "rt-stored";

        Assert.Null(await h.Session.RefreshAccessTokenAsync(rejectedToken: null, None));

        Assert.Equal(0, h.Http.CallCount(HttpMethod.Post, AuthHarness.RefreshPath));
        Assert.Equal("rt-stored", h.Secrets.Values[AuthSession.RefreshTokenKey]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
