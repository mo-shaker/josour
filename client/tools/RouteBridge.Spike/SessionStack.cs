using System.Net;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Control;
using RouteBridge.Infrastructure.Device;
using RouteBridge.Infrastructure.Security;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.Spike;

/// <summary>
/// نفس الطبقة التي يبنيها تطبيق WPF عند الإقلاع، مركّبة بلا واجهة: إعدادات على القرص، مخزن أسرار، <see cref="ApiClient"/>
/// مع <see cref="AuthenticatedHandler"/>، <see cref="AuthSession"/>، و<see cref="ControlChannel"/> حقيقي على WSS.
/// لا شيء هنا خاص بالأداة عدا مكان الملفات: بجوار التنفيذي بدل <c>%LOCALAPPDATA%</c>، حتى تُشغَّل عدة هويات
/// على الجهاز نفسه (<c>--state-dir</c>) ويُمسح كل شيء بحذف مجلد واحد (<c>--reset-device</c>).
/// </summary>
public sealed class SessionStack : IAsyncDisposable
{
    private SessionStack(
        string stateDirectory,
        AppSettingsStore settings,
        DpapiSecretStore secrets,
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
    public DpapiSecretStore Secrets { get; }
    public ApiClient Api { get; }
    public AuthSession Auth { get; }
    public ControlChannel Channel { get; }
    public DeviceInfoProvider Device { get; }

    /// <summary>
    /// يبني الطبقة ويثبّت عنوان الخادم في الإعدادات. <paramref name="resetDevice"/> يمسح هوية الجهاز والتوكنات
    /// المخزّنة أولًا، فيسجّل الدخول التالي جهازًا جديدًا (يفيد حين يُلغى الجهاز على الخادم).
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

        var secrets = new DpapiSecretStore(secretsDirectory);
        var device = new DeviceInfoProvider(typeof(SessionStack).Assembly);

        AuthSession? auth = null;
        var handler = new AuthenticatedHandler(() => auth ?? throw new InvalidOperationException("auth session not built yet"))
        {
            InnerHandler = new SocketsHttpHandler { UseProxy = true, AutomaticDecompression = DecompressionMethods.All },
        };
        var http = ApiClient.CreateHttpClient(handler, $"RouteBridge.Spike/{device.AppVersion}");
        var api = new ApiClient(http, settings);
        auth = new AuthSession(api, secrets, device);
        var channel = new ControlChannel(settings, auth, device, channelOptions);

        return new SessionStack(root, settings, secrets, http, api, auth, channel, device);
    }

    /// <summary>القائمة من <c>GET /domains</c>. المدخلات غير الصالحة تُسقَط بدل أن تُسقط الجلسة كلها.</summary>
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
/// ملتقط الإطارات الواردة: يبثها كأحداث ويسمح بانتظار إطار بعينه. القناة ترفع <c>MessageReceived</c> من حلقة
/// توزيع واحدة بترتيب الوصول، فالانتظار هنا بلا تسابق ما دام المشترك قد سُجّل قبل إرسال الطلب.
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

    /// <summary>ينتظر أول إطار يطابق <paramref name="match"/>. سجّل الانتظار قبل إرسال ما يستدعيه.</summary>
    public Task<ControlMessage> WaitAsync(Func<ControlMessage, bool> match)
    {
        var completion = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiters.Add((match, completion));
        return completion.Task;
    }

    private void OnMessage(ControlMessage message)
    {
        try { Frame?.Invoke(message); } catch { /* المستمع مسؤول عن أخطائه */ }

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
