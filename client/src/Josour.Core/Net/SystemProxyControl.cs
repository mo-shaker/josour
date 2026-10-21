using System.Collections.Concurrent;
using System.Net;

namespace Josour.Core.Net;

/// <summary>
/// What the guest points the operating system's proxy at during a session (ADR-0015): the local proxy on
/// <c>127.0.0.1:<see cref="Port"/></c> for http and https, and the names that must keep going direct.
/// </summary>
/// <param name="Port">The local proxy's port.</param>
/// <param name="ExtraBypass">Host names to exempt on top of the platform's local and private ranges (the Josour server).</param>
public sealed record SystemProxyTarget(int Port, IReadOnlyList<string> ExtraBypass)
{
    public const string LoopbackHost = "127.0.0.1";
}

/// <summary>
/// The settings as they were before a session, in the controller's own format (<see cref="Data"/> is the controller's JSON).
/// Kept on disk in the run marker so a later process can put them back.
/// </summary>
public sealed record SystemProxySnapshot(string Platform, string Data);

/// <summary>Changing the system proxy failed; <see cref="Reason"/> is a short code for the logs and the UI.</summary>
public sealed class SystemProxyException : Exception
{
    public SystemProxyException(string reason, string message, Exception? inner = null)
        : base(message, inner) => Reason = reason;

    /// <summary>
    /// <c>unsupported</c>, <c>read_failed</c>, <c>apply_failed</c>, <c>not_applied</c> (read back differently),
    /// <c>policy_locked</c> (Windows Group Policy), <c>authorization_denied</c> (macOS: the password prompt was cancelled),
    /// <c>restore_failed</c>.
    /// </summary>
    public string Reason { get; }
}

/// <summary>
/// One operating system's proxy settings. Every method is safe to call off the UI thread; none of them prompts unless
/// the platform insists (macOS may ask for an administrator's password when <c>networksetup</c> refuses a standard call).
/// </summary>
public interface ISystemProxyController
{
    /// <summary>Can this machine point its system proxy at us at all? False means no guest role here.</summary>
    bool IsSupported { get; }

    /// <summary>The settings as they are now, to put back later. Throws <see cref="SystemProxyException"/>.</summary>
    Task<SystemProxySnapshot> CaptureAsync(CancellationToken ct);

    /// <summary>Points http and https at <c>127.0.0.1:port</c> with the bypass list, and turns automatic configuration off.</summary>
    Task ApplyAsync(SystemProxyTarget target, CancellationToken ct);

    /// <summary>Do the settings read back as ours? (The check after <see cref="ApplyAsync"/>.)</summary>
    Task<bool> IsAppliedAsync(SystemProxyTarget target, CancellationToken ct);

    /// <summary>
    /// Puts <paramref name="snapshot"/> back — but only where the settings still point at <c>127.0.0.1:port</c>, so a
    /// change the user made in the meantime is never undone. Idempotent. Throws <see cref="SystemProxyException"/>.
    /// </summary>
    Task RestoreAsync(SystemProxySnapshot snapshot, int port, CancellationToken ct);
}

/// <summary>For a platform with no implementation (Linux): no guest role.</summary>
public sealed class UnsupportedSystemProxyController : ISystemProxyController
{
    public static UnsupportedSystemProxyController Instance { get; } = new();

    public bool IsSupported => false;

    public Task<SystemProxySnapshot> CaptureAsync(CancellationToken ct) => throw Unsupported();
    public Task ApplyAsync(SystemProxyTarget target, CancellationToken ct) => throw Unsupported();
    public Task<bool> IsAppliedAsync(SystemProxyTarget target, CancellationToken ct) => Task.FromResult(false);
    public Task RestoreAsync(SystemProxySnapshot snapshot, int port, CancellationToken ct) => Task.CompletedTask;

    private static SystemProxyException Unsupported()
        => new("unsupported", "this operating system's proxy settings cannot be changed by Josour");
}

/// <summary>
/// The local proxy ports this process is pointing the system at. While a session is active, the system proxy says
/// <c>127.0.0.1:port</c> to every program — this one included — and the app's own traffic (the API, the control channel,
/// the proxy's own direct path) must never enter its own proxy. <see cref="LoopSafeWebProxy"/> reads this set.
/// </summary>
public static class OwnProxyPorts
{
    private static readonly ConcurrentDictionary<int, byte> Ports = new();

    public static void Add(int port) => Ports[port] = 0;
    public static void Remove(int port) => Ports.TryRemove(port, out _);
    public static bool Contains(int port) => Ports.ContainsKey(port);

    /// <summary>Is this proxy address one of ours (loopback, on a registered port)?</summary>
    public static bool IsOwn(Uri? proxy)
        => proxy is not null && proxy.IsLoopback && Contains(proxy.Port);
}

/// <summary>
/// The system proxy as every client in this process sees it (<see cref="HttpClient.DefaultProxy"/> is replaced by this at
/// start-up), minus our own listener: a destination the system would send to <c>127.0.0.1:ourport</c> goes direct instead.
/// Any other proxy — a company's, a local tool's — is used as before.
/// </summary>
public sealed class LoopSafeWebProxy : IWebProxy
{
    private readonly IWebProxy _inner;

    public LoopSafeWebProxy(IWebProxy inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Wraps <see cref="HttpClient.DefaultProxy"/> once; calling it again changes nothing.</summary>
    public static void InstallAsDefault()
    {
        if (HttpClient.DefaultProxy is LoopSafeWebProxy) return;
        HttpClient.DefaultProxy = new LoopSafeWebProxy(HttpClient.DefaultProxy);
    }

    public ICredentials? Credentials
    {
        get => _inner.Credentials;
        set => _inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination)
    {
        var proxy = _inner.GetProxy(destination);
        return OwnProxyPorts.IsOwn(proxy) ? null : proxy;
    }

    public bool IsBypassed(Uri host)
    {
        if (_inner.IsBypassed(host)) return true;
        try { return OwnProxyPorts.IsOwn(_inner.GetProxy(host)); }
        catch { return false; }
    }
}
