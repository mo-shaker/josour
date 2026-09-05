namespace RouteBridge.Infrastructure.Session;

/// <summary>
/// The allow-list version a session was created under could not be fetched, so the session must not run.
/// <para>
/// docs/api.md guarantees allow-list versions are immutable and never pruned, so this is an availability failure (the
/// server is unreachable, the token was refused, the version is malformed) rather than an expected outcome. It still has
/// to be handled, and the handling is deliberate: a tunnel enforces the list, so it may neither substitute the current
/// list (rules nobody agreed to) nor an empty one (a silent denial the user cannot distinguish from a broken network).
/// The session is refused with <see cref="FailureReason"/> instead.
/// </para>
/// </summary>
public sealed class AllowlistUnavailableException : Exception
{
    /// <summary>The value that goes into <c>session.connect_failed.diagnostics.failure_reason</c>.</summary>
    public const string FailureReason = "allowlist_unavailable";

    public AllowlistUnavailableException(int version, string detail, Exception? inner = null)
        : base($"Allow-list version {version} could not be loaded: {detail}", inner)
    {
        Version = version;
    }

    /// <summary>The <c>allowlist_version</c> the session named.</summary>
    public int Version { get; }
}
