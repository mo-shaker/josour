using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// An <see cref="IApiClient"/> for the session and disclosure tests: only the two <c>GET /domains</c> shapes are scripted
/// (that is all the coordinator and the host's disclosure call), every other endpoint throws so an accidental call is loud
/// rather than silent.
/// </summary>
public sealed class FakeApiClient : IApiClient
{
    public int DomainsVersion { get; set; } = 7;

    public List<string> DomainEntries { get; } = new() { "example.com", "=exact.com", "portal.corp:8443" };

    /// <summary>Set to make <c>GET /domains</c> fail the way an unreachable server does.</summary>
    public Exception? DomainsError { get; set; }

    /// <summary>Every <c>knownVersion</c> the coordinator sent (null on the first call): proves the version cache.</summary>
    public List<int?> DomainsCalls { get; } = new();

    /// <summary>Every version asked for through <c>GET /domains?version=N</c>.</summary>
    public List<int> DomainsVersionCalls { get; } = new();

    /// <summary>Set to make the versioned fetch fail (unreachable server, 404 for a version the server dropped, …).</summary>
    public Exception? DomainsVersionError { get; set; }

    /// <summary>Delays the versioned fetch, so a test can drive the disclosure's own timeout.</summary>
    public TimeSpan DomainsVersionDelay { get; set; }

    /// <summary>Answers with this version instead of the one asked for (the server contradicting itself).</summary>
    public int? DomainsVersionOverride { get; set; }

    public async Task<DomainsDto> GetDomainsVersionAsync(int version, CancellationToken ct)
    {
        DomainsVersionCalls.Add(version);
        if (DomainsVersionDelay > TimeSpan.Zero)
        {
            await Task.Delay(DomainsVersionDelay, ct);
        }

        if (DomainsVersionError is { } error)
        {
            throw error;
        }

        return new DomainsDto(DomainsVersionOverride ?? version, DomainEntries.ToList());
    }

    public Task<DomainsResult> GetDomainsAsync(int? knownVersion, CancellationToken ct)
    {
        DomainsCalls.Add(knownVersion);
        if (DomainsError is { } error)
        {
            return Task.FromException<DomainsResult>(error);
        }

        if (knownVersion == DomainsVersion)
        {
            return Task.FromResult(DomainsResult.NotModified);
        }

        return Task.FromResult(DomainsResult.From(new DomainsDto(DomainsVersion, DomainEntries.ToList()), $"\"{DomainsVersion}\""));
    }

    public Task<AuthResponse> LoginAsync(string email, string password, DeviceLoginInfo device, CancellationToken ct) => throw new NotSupportedException();

    public Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct) => throw new NotSupportedException();

    public Task LogoutAsync(string refreshToken, CancellationToken ct) => throw new NotSupportedException();

    public Task<UserDto> GetMeAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceDto>> GetMyDevicesAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteMyDeviceAsync(Guid deviceId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<HostInfoDto>> GetHostsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionDto>> GetMySessionsAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

    public Task<ProbeResult> ProbeAsync(string ip, int port, CancellationToken ct) => throw new NotSupportedException();

    // ---- POST /diagnostics ----

    /// <summary>One recorded <c>POST /diagnostics</c>.</summary>
    public sealed record PostedDiagnostics(Guid? SessionId, string? Role, IReadOnlyDictionary<string, object?> Data);

    /// <summary>Every diagnostics post, in order.</summary>
    public List<PostedDiagnostics> DiagnosticsPosts { get; } = new();

    /// <summary>Set to make the post fail the way an unreachable server does.</summary>
    public Exception? DiagnosticsError { get; set; }

    public Task<DiagnosticsAccepted> PostDiagnosticsAsync(Guid? sessionId, string? role, IReadOnlyDictionary<string, object?> data, CancellationToken ct)
    {
        DiagnosticsPosts.Add(new PostedDiagnostics(sessionId, role, new Dictionary<string, object?>(data, StringComparer.Ordinal)));
        if (DiagnosticsError is { } error)
        {
            return Task.FromException<DiagnosticsAccepted>(error);
        }

        return Task.FromResult(new DiagnosticsAccepted(Guid.NewGuid()));
    }
}
