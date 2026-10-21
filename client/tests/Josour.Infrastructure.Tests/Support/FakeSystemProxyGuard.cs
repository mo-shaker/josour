using Josour.Core.Net;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>The system proxy as the coordinator sees it, recorded instead of changed.</summary>
public sealed class FakeSystemProxyGuard : ISystemProxyGuard
{
    private int _applied;

    public bool IsSupported { get; set; } = true;

    /// <summary>When set, <see cref="ApplyAsync"/> fails with this reason (<c>policy_locked</c>, <c>authorization_denied</c>…).</summary>
    public string? FailWith { get; set; }

    public bool IsApplied => Volatile.Read(ref _applied) == 1;

    public int? Port { get; private set; }
    public int ApplyCount { get; private set; }
    public int RestoreCount { get; private set; }

    public Task ApplyAsync(int port, IReadOnlyList<string> extraBypass, CancellationToken ct)
    {
        ApplyCount++;
        if (FailWith is { } reason) throw new SystemProxyException(reason, "test failure: " + reason);
        Port = port;
        Volatile.Write(ref _applied, 1);
        return Task.CompletedTask;
    }

    public Task<bool> RestoreAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _applied, 0) == 0) return Task.FromResult(false);
        RestoreCount++;
        return Task.FromResult(true);
    }
}

public sealed class FakeCheckPageOpener : ICheckPageOpener
{
    private readonly List<string> _opened = new();

    public bool OpenOnConnect { get; set; } = true;
    public bool Opens { get; set; } = true;

    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_opened) return _opened.ToArray();
        }
    }

    public bool TryOpen(string url)
    {
        lock (_opened) _opened.Add(url);
        return Opens;
    }
}
