using Josour.Infrastructure.Api;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>ApiClient + AuthenticatedHandler + AuthSession wired exactly like the app, over a <see cref="FakeHttpMessageHandler"/>.</summary>
public sealed class AuthHarness : IDisposable
{
    public static readonly Guid UserId = new("aaaaaaaa-0000-4000-8000-000000000001");
    public static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");

    public const string LoginPath = "/api/v1/auth/login";
    public const string RefreshPath = "/api/v1/auth/refresh";
    public const string LogoutPath = "/api/v1/auth/logout";
    public const string MePath = "/api/v1/me";

    private readonly HttpClient _http;

    public AuthHarness(string serverUrl = "https://server.test")
    {
        Http = new FakeHttpMessageHandler();
        Settings = new InMemoryAppSettingsStore(serverUrl);
        Secrets = new InMemorySecretStore();
        Device = new FakeDeviceInfoProvider();
        Time = new ManualTimeProvider();

        AuthSession? session = null;
        var handler = new AuthenticatedHandler(() => session!) { InnerHandler = Http };
        _http = ApiClient.CreateHttpClient(handler, "Josour-tests/0.2.0");
        Api = new ApiClient(_http, Settings);
        session = new AuthSession(Api, Secrets, Device, logger: null, Time);
        Session = session;

        Session.Changed += (_, _) => ChangedCount++;
        Session.SignedOut += (_, e) => SignedOutReasons.Add(e.Reason);
    }

    public FakeHttpMessageHandler Http { get; }

    public InMemoryAppSettingsStore Settings { get; }

    public InMemorySecretStore Secrets { get; }

    public FakeDeviceInfoProvider Device { get; }

    public ManualTimeProvider Time { get; }

    public ApiClient Api { get; }

    public AuthSession Session { get; }

    public int ChangedCount { get; private set; }

    public List<SignOutReason> SignedOutReasons { get; } = new();

    public HttpClient HttpClient => _http;

    public static string AuthJson(string accessToken, string refreshToken, Guid? deviceId = null, string? deviceSecret = null, int expiresIn = 900)
    {
        var secret = deviceSecret is null ? "null" : "\"" + deviceSecret + "\"";
        return "{\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken + "\",\"expires_in\":" + expiresIn
            + ",\"user\":{\"id\":\"" + UserId + "\",\"email\":\"guest@example.com\",\"display_name\":\"Guest\",\"role\":\"user\"}"
            + ",\"device\":{\"id\":\"" + (deviceId ?? DeviceId) + "\",\"name\":\"TEST-PC\",\"secret\":" + secret + "}}";
    }

    public const string MeJson = """{"id":"aaaaaaaa-0000-4000-8000-000000000001","email":"guest@example.com","display_name":"Guest","role":"user"}""";

    /// <summary>Scripts a successful first-device login and signs in (at-1 / rt-1, secret dev-secret-1).</summary>
    public async Task SignInFreshAsync()
    {
        Http.On(HttpMethod.Post, LoginPath, _ => FakeHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, AuthJson("at-1", "rt-1", DeviceId, "dev-secret-1")));
        await Session.SignInAsync("guest@example.com", "Guest-pass-1234", CancellationToken.None);
    }

    public void Dispose() => _http.Dispose();
}
