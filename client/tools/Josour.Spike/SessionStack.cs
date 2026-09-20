using System.Net;
using Josour.Core.Allowlist;
using Josour.Core.Control;
using Josour.Core.Security;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Security;
using Josour.Infrastructure.Settings;

namespace Josour.Spike;

/// <summary>
/// The same layer the WPF application builds at startup, assembled with no interface: settings on disk, a secret store, an <see cref="ApiClient"/>
/// with an <see cref="AuthenticatedHandler"/>, an <see cref="AuthSession"/>, and a real <see cref="ControlChannel"/> over WSS.
/// Nothing here is specific to the tool except where the files live: beside the executable instead of <c>%LOCALAPPDATA%</c>, so several identities can be run
/// on the same machine (<c>--state-dir</c>) and everything is wiped by deleting one directory (<c>--reset-device</c>).
/// </summary>
public sealed class SessionStack : IAsyncDisposable
{
    private SessionStack(
        string stateDirectory,
        AppSettingsStore settings,
        ISecretStore secrets,
        HttpClient http,
        ApiClient api,
        AuthSession auth,
        ControlChannel channel,
        DeviceInfoProvider device)
    {
        StateDirectory = stateDirectory;
        Settings = settings;
        Secrets = secrets;
        _http = http;
        Api = api;
        Auth = auth;
        Channel = channel;
        Device = device;
    }

    private readonly HttpClient _http;

    public string StateDirectory { get; }
    public AppSettingsStore Settings { get; }
    public ISecretStore Secrets { get; }
    public ApiClient Api { get; }
    public AuthSession Auth { get; }
    public ControlChannel Channel { get; }
    public DeviceInfoProvider Device { get; }

    /// <summary>
    /// Builds the layer and pins the server's address in the settings. <paramref name="resetDevice"/> wipes the device identity and the stored
    /// tokens first, so the next sign-in registers a new device (useful when the device has been revoked on the server).
    /// </summary>
    public static async Task<SessionStack> CreateAsync(string apiBaseUrl, string stateDirectory, bool resetDevice, ControlChannelOptions? channelOptions, CancellationToken ct)
    {
        if (!ServerUrl.TryNormalize(apiBaseUrl, out var baseUri)) throw new UsageException($"--api '{apiBaseUrl}' is not an absolute http(s) URL");
        if (!ServerUrl.IsAllowed(baseUri)) throw new UsageException("--api must be https (plain http is only allowed for loopback)");

        var root = Path.GetFullPath(stateDirectory);
        var secretsDirectory = Path.Combine(root, "secrets");
        if (resetDevice && Directory.Exists(secretsDirectory))
        {
            Directory.Delete(secretsDirectory, recursive: true);
        }

        Directory.CreateDirectory(root);
        var settings = new AppSettingsStore(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(settings.Current with { ServerUrl = baseUri.ToString() }, ct).ConfigureAwait(false);

        // The same store the app uses, chosen the same way — DPAPI on Windows, an owner-only file elsewhere — so
        // a session driven from here and a session driven from the window are looking at the same secrets.
        ISecretStore secrets = OperatingSystem.IsWindows()
            ? new DpapiSecretStore(secretsDirectory)
            : new FileSecretStore(secretsDirectory);
        var device = new DeviceInfoProvider(typeof(SessionStack).Assembly);

        AuthSession? auth = null;
        var handler = new AuthenticatedHandler(() => auth ?? throw new InvalidOperationException("auth session not built yet"))
        {
            InnerHandler = new SocketsHttpHandler { UseProxy = true, AutomaticDecompression = DecompressionMethods.All },
        };
        var http = ApiClient.CreateHttpClient(handler, $"Josour.Spike/{device.AppVersion}");
        var api = new ApiClient(http, settings);
        auth = new AuthSession(api, secrets, device);
        var channel = new ControlChannel(settings, auth, device, channelOptions);

        return new SessionStack(root, settings, secrets, http, api, auth, channel, device);
    }

    /// <summary>The list from <c>GET /domains</c>. Invalid entries are dropped rather than dropping the whole session.</summary>
    public async Task<(AllowlistMatcher Allowlist, int Version, int Skipped)> LoadAllowlistAsync(CancellationToken ct)
    {
        var result = await Api.GetDomainsAsync(null, ct).ConfigureAwait(false);
        var domains = result.Domains;
        if (domains is null) return (AllowlistMatcher.Parse(0, Array.Empty<string>()), 0, 0);

        var entries = new List<AllowlistEntry>();
        var skipped = 0;
        foreach (var raw in domains.Entries)
        {
            if (AllowlistMatcher.TryParseEntry(raw, out var entry, out _)) entries.Add(entry);
            else skipped++;
        }

        return (new AllowlistMatcher(domains.Version, entries), domains.Version, skipped);
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}

/// <summary>
/// A catcher for the inbound frames: it broadcasts them as events and allows waiting for one particular frame. The channel raises <c>MessageReceived</c> from one
/// dispatch loop in arrival order, so waiting here has no race as long as the subscriber was registered before the request was sent.
/// </summary>
public sealed class FrameWatcher : IDisposable
{
    private readonly IControlChannel _channel;
    private readonly List<(Func<ControlMessage, bool> Match, TaskCompletionSource<ControlMessage> Completion)> _waiters = new();
    private readonly object _gate = new();

    public FrameWatcher(IControlChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += OnMessage;
    }

    public event Action<ControlMessage>? Frame;

    /// <summary>Waits for the first frame matching <paramref name="match"/>. Register the wait before sending what triggers it.</summary>
    public Task<ControlMessage> WaitAsync(Func<ControlMessage, bool> match)
    {
        var completion = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiters.Add((match, completion));
        return completion.Task;
    }

    private void OnMessage(ControlMessage message)
    {
        try { Frame?.Invoke(message); } catch { /* the listener is responsible for its own errors */ }

        List<TaskCompletionSource<ControlMessage>>? matched = null;
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                bool hit;
                try { hit = _waiters[i].Match(message); }
                catch { hit = false; }
                if (!hit) continue;
                (matched ??= new List<TaskCompletionSource<ControlMessage>>()).Add(_waiters[i].Completion);
                _waiters.RemoveAt(i);
            }
        }

        if (matched is null) return;
        foreach (var completion in matched) completion.TrySetResult(message);
    }

    public void Dispose() => _channel.MessageReceived -= OnMessage;
}
