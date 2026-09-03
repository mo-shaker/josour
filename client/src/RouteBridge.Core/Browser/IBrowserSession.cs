namespace RouteBridge.Core.Browser;

public enum BrowserKind { Chrome, Edge }

public sealed record BrowserLaunchOptions(BrowserKind Kind, int ProxyPort, string ProfileDirectory, string ProbeUrl);

public enum BrowserLaunchFailure { NotFound, ManagedByPolicy, InstanceHandoff, ProbeTimeout, Other }

public sealed record BrowserLaunchResult(bool Success, BrowserLaunchFailure? Failure, string? Detail);

/// <summary>متصفح العمل: تشغيل داخل Job Object وإغلاق مهذب ثم قسري. المسار B يوفر التنفيذ في RouteBridge.Browser.</summary>
public interface IBrowserSession : IAsyncDisposable
{
    bool IsRunning { get; }
    /// <summary>معرّفات عمليات المتصفح داخل الـ Job، لفحص PID المالك في الـ Proxy.</summary>
    bool OwnsProcess(int pid);
    Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct);
    Task CloseAsync(TimeSpan graceful, CancellationToken ct);
}
