using RouteBridge.Core.Security;

namespace RouteBridge.Infrastructure.Tests.Support;

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly object _gate = new();

    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>"set:key" / "remove:key" in call order.</summary>
    public List<string> Log { get; } = new();

    public int SetCount(string key) => Log.Count(entry => entry == "set:" + key);

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        lock (_gate)
        {
            Values[key] = value;
            Log.Add("set:" + key);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            Values.Remove(key);
            Log.Add("remove:" + key);
        }

        return Task.CompletedTask;
    }
}
