using System.Collections.Concurrent;
using System.Net;
using Josour.Core.Control;
using Josour.Core.Security;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Control.Mock;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Settings;
using Josour.Mobile.Platform;
using Josour.Mobile.Services;
using Josour.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Josour.Mobile.Tests;

internal sealed class FakeNetwork : INetworkFacts
{
    public NetworkSnapshot Current { get; private set; } = new(NetworkKind.WiFi, false, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());
    public event Action<NetworkSnapshot>? Changed;
    public event Action<ConnectivitySignal>? Signalled;

    public void Move(NetworkSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
        Signalled?.Invoke(ConnectivitySignal.NetworkAddressChanged);
    }
}

internal sealed class FakeHosting : IHostingService
{
    public List<string> Calls { get; } = new();
    public bool Running { get; private set; }
    public bool LastSessionActive { get; private set; }

    public void Show(string title, string text, bool sessionActive)
    {
        Running = true;
        LastSessionActive = sessionActive;
        Calls.Add("show:" + title);
    }

    public void Stop()
    {
        Running = false;
        Calls.Add("stop");
    }
}

internal sealed class FakeNotifier : IMobileNotifier
{
    public ConcurrentQueue<string> Events { get; } = new();
    public void ShowIncomingRequest(Guid requestId, string title, string body) => Events.Enqueue("request:" + requestId);
    public void CancelIncomingRequest(Guid requestId) => Events.Enqueue("cancel:" + requestId);
    public void ShowInfo(string title, string body) => Events.Enqueue("info:" + title);
}

internal sealed class FakePermissions : IMobilePermissions
{
    public bool NeedsNotificationPermission { get; set; }
    public bool IsBatteryOptimized { get; set; }
    public void RequestNotificationPermission() { }
    public void RequestIgnoreBatteryOptimizations() { }
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}

internal sealed class MemorySecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value, CancellationToken ct) { _values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct) { _values.TryRemove(key, out _); return Task.CompletedTask; }
}

internal sealed class FakeDevice : IDeviceInfoProvider
{
    public string DeviceName => "Pixel 8";
    public string OsVersion => "Android 15";
    public string OsBuild => "API 35";
    public string AppVersion => "2.1.1";
    public DeviceInfo GetDeviceInfo() => new(DeviceName, OsVersion, OsBuild, AppVersion);
}

/// <summary>
/// The phone's real container (<see cref="MobileRuntime.Configure"/>) on fakes: the platform, the settings and the
/// secrets live in a temporary folder and memory, and the control channel is the built-in simulated server.
/// </summary>
internal sealed class TestPhone : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "josour-mobile-tests-" + Guid.NewGuid().ToString("N"));

    public TestPhone()
    {
        Directory.CreateDirectory(_folder);
        Platform = new MobilePlatform(new FakeDevice(), _ => new MemorySecrets(), Network, Hosting, Notifier, Permissions);
        var services = new ServiceCollection();
        MobileRuntime.Configure(services, Platform);
        services.AddSingleton<IAppSettingsStore>(new AppSettingsStore(Path.Combine(_folder, "settings.json")));
        services.AddSingleton(new HostAvailabilityFlag(Path.Combine(_folder, "host-available")));
        services.AddSingleton<IControlChannel>(Channel);
        Services = services.BuildServiceProvider();
    }

    public FakeNetwork Network { get; } = new();
    public FakeHosting Hosting { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakePermissions Permissions { get; } = new();
    public MockControlChannel Channel { get; } = new(MockControlChannelOptions.Default with { AutoAcceptGuestRequests = false });
    public MobilePlatform Platform { get; }
    public ServiceProvider Services { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
