namespace Josour.Core.Browser;

public enum BrowserKind { Chrome, Edge }

public sealed record BrowserLaunchOptions(BrowserKind Kind, int ProxyPort, string ProfileDirectory, string ProbeUrl);

public enum BrowserLaunchFailure { NotFound, ManagedByPolicy, InstanceHandoff, ProbeTimeout, Other }

public sealed record BrowserLaunchResult(bool Success, BrowserLaunchFailure? Failure, string? Detail);

/// <summary>The work browser: launched inside a Job Object, with a polite close then a forced one. Track B provides the implementation in Josour.Browser.</summary>
public interface IBrowserSession : IAsyncDisposable
{
    bool IsRunning { get; }
    /// <summary>The browser's process ids inside the job, for the owning-PID check in the proxy.</summary>
    bool OwnsProcess(int pid);
    Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct);
    Task CloseAsync(TimeSpan graceful, CancellationToken ct);
}
