using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// An <see cref="IApiClient"/> for the session tests: only <c>GET /domains</c> is scripted (that is all the coordinator calls),
/// every other endpoint throws so an accidental call is loud rather than silent.
/// </summary>
public sealed class FakeApiClient : IApiClient
{
    public int DomainsVersion { get; set; } = 7;

    public List<string> DomainEntries { get; } = new() { "example.com", "=exact.com", "portal.corp:8443" };

    /// <summary>Set to make <c>GET /domains</c> fail the way an unreachable server does.</summary>
    public Exception? DomainsError { get; set; }

    /// <summary>Every <c>knownVersion</c> the coordinator sent (null on the first call): proves the version cache.</summary>
    public List<int?> DomainsCalls { get; } = new();

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
}
