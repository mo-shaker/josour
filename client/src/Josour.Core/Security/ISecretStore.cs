namespace Josour.Core.Security;

/// <summary>Storing the device's secrets (the refresh token, the device secret) protected by DPAPI on Windows. Track C provides the implementation.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}
