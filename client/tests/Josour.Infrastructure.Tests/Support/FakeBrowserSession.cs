using Josour.Core.Browser;
using Josour.Infrastructure.Session;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>An <see cref="IBrowserSession"/> that records what it was asked to launch instead of starting a real process.</summary>
public sealed class FakeBrowserSession : IBrowserSession
{
    public BrowserLaunchResult Result { get; set; } = new(true, null, null);

    public BrowserLaunchOptions? Options { get; private set; }

    public bool IsRunning { get; private set; }

    public int LaunchCount { get; private set; }

    public int CloseCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool OwnsProcess(int pid) => IsRunning && pid == 4242;

    public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        LaunchCount++;
        Options = options;
        IsRunning = Result.Success;
        return Task.FromResult(Result);
    }

    public Task CloseAsync(TimeSpan graceful, CancellationToken ct)
    {
        CloseCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A <see cref="IWorkBrowserProvider"/> over <see cref="FakeBrowserSession"/>: the test says which browsers are "installed"
/// and how each launch turns out, and sees every session that was created (a reopen creates a new one).
/// </summary>
public sealed class FakeWorkBrowserProvider : IWorkBrowserProvider
{
    public string ProfileDirectory { get; set; } = @"C:\Users\test\AppData\Local\Josour\BrowserProfile";

    public List<BrowserKind> Installed { get; } = new() { BrowserKind.Chrome };

    /// <summary>Result of the next launch (per kind); the default is a successful launch.</summary>
    public Dictionary<BrowserKind, BrowserLaunchResult> Results { get; } = new();

    public List<FakeBrowserSession> Created { get; } = new();

    public FakeBrowserSession Last => Created[^1];

    public IReadOnlyList<BrowserKind> Preference() => Installed.ToList();

    public IBrowserSession Create(BrowserKind kind)
    {
        var session = new FakeBrowserSession();
        if (Results.TryGetValue(kind, out var result))
        {
            session.Result = result;
        }

        Created.Add(session);
        return session;
    }
}
