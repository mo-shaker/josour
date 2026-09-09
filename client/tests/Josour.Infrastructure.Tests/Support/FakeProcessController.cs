using Josour.Infrastructure.Session;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>A process table the test writes by hand: what is running, and what closing it does.</summary>
public sealed class FakeProcessController : IProcessController
{
    private readonly Dictionary<int, ProcessSnapshot> _running = new();

    /// <summary>Every process id <see cref="CloseAsync"/> was asked to close, in order.</summary>
    public List<int> Closed { get; } = new();

    /// <summary>Set to make closing fail the way a process this account may not touch does.</summary>
    public bool CloseSucceeds { get; set; } = true;

    public void Add(int processId, string name, DateTimeOffset startedAtUtc) =>
        _running[processId] = new ProcessSnapshot(processId, name, startedAtUtc);

    public bool IsRunning(int processId) => _running.ContainsKey(processId);

    public ProcessSnapshot? Find(int processId) => _running.TryGetValue(processId, out var snapshot) ? snapshot : null;

    public Task<bool> CloseAsync(int processId, TimeSpan graceful, CancellationToken ct)
    {
        Closed.Add(processId);
        if (!CloseSucceeds)
        {
            return Task.FromResult(false);
        }

        _running.Remove(processId);
        return Task.FromResult(true);
    }
}

/// <summary>An <see cref="IWorkBrowserRunMarkerStore"/> in memory, so the guard's tests touch no disk.</summary>
public sealed class InMemoryWorkBrowserRunMarkerStore : IWorkBrowserRunMarkerStore
{
    public WorkBrowserRunMarker? Marker { get; set; }

    public int Writes { get; private set; }

    public int Clears { get; private set; }

    public WorkBrowserRunMarker? Read() => Marker;

    public void Write(WorkBrowserRunMarker marker)
    {
        Marker = marker;
        Writes++;
    }

    public void Clear()
    {
        Marker = null;
        Clears++;
    }
}
