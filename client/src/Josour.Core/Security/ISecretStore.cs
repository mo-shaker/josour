namespace Josour.Core.Security;

/// <summary>تخزين أسرار الجهاز (refresh token، device secret) محميًا بـ DPAPI على Windows. المسار C يوفر التنفيذ.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}
