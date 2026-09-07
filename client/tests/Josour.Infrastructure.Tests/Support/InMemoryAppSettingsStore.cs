using Josour.Infrastructure.Settings;

namespace Josour.Infrastructure.Tests.Support;

public sealed class InMemoryAppSettingsStore : IAppSettingsStore
{
    public InMemoryAppSettingsStore(string serverUrl = "https://server.test")
    {
        Current = new AppSettings { ServerUrl = serverUrl };
    }

    public AppSettings Current { get; set; }

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}
