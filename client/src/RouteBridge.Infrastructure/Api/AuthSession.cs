using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBridge.Core.Security;
using RouteBridge.Infrastructure.Device;

namespace RouteBridge.Infrastructure.Api;

/// <summary>
/// <see cref="IAuthSession"/> + <see cref="IAccessTokenSource"/>. Secret-store keys: <see cref="RefreshTokenKey"/>, <see cref="DeviceIdKey"/>,
/// <see cref="DeviceSecretKey"/> (the server URL lives in settings). Tokens and secrets are never logged.
/// </summary>
public sealed class AuthSession : IAuthSession, IAccessTokenSource
{
    public const string RefreshTokenKey = "refresh_token";
    public const string DeviceIdKey = "device_id";
    public const string DeviceSecretKey = "device_secret";

    /// <summary>Refresh proactively when less than this remains (<see cref="GetValidAccessTokenAsync"/>).</summary>
    public static readonly TimeSpan ExpirySlack = TimeSpan.FromSeconds(60);

    // Server-side field limits of POST /auth/login device.* (backend app/schemas/auth.py); longer values are cut, never rejected.
    public const int MaxDeviceNameLength = 100;
    public const int MaxOsVersionLength = 100;
    public const int MaxOsBuildLength = 50;

    private readonly IApiClient _api;
    private readonly ISecretStore _secrets;
    private readonly IDeviceInfoProvider _deviceInfo;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthSession> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();

    private string? _accessToken;
    private DateTimeOffset? _expiresAt;
    private UserDto? _user;
    private Guid? _deviceId;

    public AuthSession(
        IApiClient api,
        ISecretStore secrets,
        IDeviceInfoProvider deviceInfo,
        ILogger<AuthSession>? logger = null,
        TimeProvider? time = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _deviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
        _logger = logger ?? NullLogger<AuthSession>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public bool IsSignedIn => AccessToken is not null;

    public UserDto? CurrentUser
    {
        get
        {
            lock (_stateLock)
            {
                return _user;
            }
        }
    }

    public Guid? DeviceId
    {
        get
        {
            lock (_stateLock)
            {
                return _deviceId;
            }
        }
    }

    public string? AccessToken
    {
        get
        {
            lock (_stateLock)
            {
                return _accessToken;
            }
        }
    }

    public DateTimeOffset? AccessTokenExpiresAt
    {
        get
        {
            lock (_stateLock)
            {
                return _expiresAt;
            }
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<SignedOutEventArgs>? SignedOut;

    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        var refreshToken = await _secrets.GetAsync(RefreshTokenKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogInformation("No stored session to restore");
            return false;
        }

        try
        {
            var response = await _api.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            await ApplyAsync(response, ct).ConfigureAwait(false);
            _logger.LogInformation("Session restored for {Email} on device {DeviceId}", response.User.Email, response.Device.Id);
            RaiseChanged();
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Stored session rejected ({Code}); clearing it", ex.Code);
            await ClearSessionAsync(ex.Code, ct).ConfigureAwait(false);
            return false;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Session restore failed with {Status} {Code}; keeping the stored session for a later retry", (int)ex.StatusCode, ex.Code);
            return false;
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Session restore skipped: {Reason}", ex.Message);
            return false;
        }
    }

    public async Task SignInAsync(string email, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);

        var storedId = await GetStoredDeviceIdAsync(ct).ConfigureAwait(false);
        var storedSecret = await _secrets.GetAsync(DeviceSecretKey, ct).ConfigureAwait(false);
        var hasIdentity = storedId is not null && !string.IsNullOrEmpty(storedSecret);

        var info = _deviceInfo.GetDeviceInfo();
        var device = new DeviceLoginInfo(
            hasIdentity ? storedId : null,
            hasIdentity ? storedSecret : null,
            Clamp(info.DeviceName, MaxDeviceNameLength) ?? "unknown-device",
            Clamp(info.OsVersion, MaxOsVersionLength) ?? "unknown",
            Clamp(info.OsBuild, MaxOsBuildLength));

        _logger.LogInformation("Signing in {Email} ({DeviceMode})", email, hasIdentity ? "known device" : "new device");

        AuthResponse response;
        try
        {
            response = await _api.LoginAsync(email, password, device, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (hasIdentity && ex.Code is ApiErrorCodes.DeviceRevoked or ApiErrorCodes.Unauthorized)
        {
            // The server no longer accepts this device identity (revoked, or the secret is stale). Drop it so the
            // next sign-in registers a fresh device instead of failing forever; the error still reaches the UI.
            _logger.LogWarning("Device identity {DeviceId} rejected ({Code}); discarding it", storedId, ex.Code);
            await ClearDeviceIdentityAsync(ct).ConfigureAwait(false);
            throw;
        }

        await ApplyAsync(response, ct).ConfigureAwait(false);
        _logger.LogInformation("Signed in as {Email} on device {DeviceId}", response.User.Email, response.Device.Id);
        RaiseChanged();
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var refreshToken = await _secrets.GetAsync(RefreshTokenKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                await _api.LogoutAsync(refreshToken, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                _logger.LogWarning("Logout rejected by the server ({Code}); clearing locally anyway", ex.Code);
            }
            catch (ApiUnavailableException ex)
            {
                _logger.LogWarning("Logout could not reach the server ({Reason}); clearing locally anyway", ex.Message);
            }
        }

        var wasSignedIn = IsSignedIn;
        await ClearSessionAsync(reasonCode: null, ct).ConfigureAwait(false);
        if (!wasSignedIn)
        {
            RaiseChanged();
        }

        _logger.LogInformation("Signed out");
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct)
    {
        string? token;
        DateTimeOffset? expiresAt;
        lock (_stateLock)
        {
            token = _accessToken;
            expiresAt = _expiresAt;
        }

        if (token is null)
        {
            return null;
        }

        if (expiresAt is null || expiresAt.Value - _time.GetUtcNow() > ExpirySlack)
        {
            return token;
        }

        _logger.LogDebug("Access token about to expire; refreshing proactively");
        return await RefreshAccessTokenAsync(token, ct).ConfigureAwait(false);
    }

    public async Task<string?> RefreshAccessTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = AccessToken;
            if (current is not null && !string.Equals(current, rejectedToken, StringComparison.Ordinal))
            {
                // Another caller already refreshed while we were waiting for the gate.
                return current;
            }

            var refreshToken = await _secrets.GetAsync(RefreshTokenKey, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogWarning("Cannot refresh: no refresh token stored");
                await ClearSessionAsync(ApiErrorCodes.Unauthorized, ct).ConfigureAwait(false);
                return null;
            }

            try
            {
                var response = await _api.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
                await ApplyAsync(response, ct).ConfigureAwait(false);
                _logger.LogInformation("Access token refreshed");
                RaiseChanged();
                return response.AccessToken;
            }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Refresh rejected ({Code}); signing out", ex.Code);
                await ClearSessionAsync(ex.Code, ct).ConfigureAwait(false);
                return null;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning("Refresh failed with {Status} {Code}; session kept", (int)ex.StatusCode, ex.Code);
                return null;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---------- state ----------

    private async Task ApplyAsync(AuthResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrEmpty(response.AccessToken) || string.IsNullOrEmpty(response.RefreshToken))
        {
            throw new ApiException(HttpStatusCode.OK, ApiErrorCodes.InvalidResponse, "The auth response is missing its tokens.");
        }

        // Persist first, so a crash between the two steps never leaves a token in memory that the disk does not know.
        await _secrets.SetAsync(RefreshTokenKey, response.RefreshToken, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response.Device.Secret))
        {
            await _secrets.SetAsync(DeviceIdKey, response.Device.Id.ToString("D"), ct).ConfigureAwait(false);
            await _secrets.SetAsync(DeviceSecretKey, response.Device.Secret, ct).ConfigureAwait(false);
            _logger.LogInformation("Device registered as {DeviceId}; secret stored", response.Device.Id);
        }
        else
        {
            var storedId = await GetStoredDeviceIdAsync(ct).ConfigureAwait(false);
            if (storedId is not null && storedId != response.Device.Id)
            {
                _logger.LogWarning("Server device id {ServerId} differs from the stored {StoredId}", response.Device.Id, storedId);
            }
        }

        lock (_stateLock)
        {
            _accessToken = response.AccessToken;
            _expiresAt = _time.GetUtcNow().AddSeconds(Math.Max(0, response.ExpiresIn));
            _user = response.User;
            _deviceId = response.Device.Id;
        }
    }

    /// <summary>Forgets the tokens (keeps the device identity unless the device was revoked) and raises the events.</summary>
    private async Task ClearSessionAsync(string? reasonCode, CancellationToken ct)
    {
        bool wasSignedIn;
        lock (_stateLock)
        {
            wasSignedIn = _accessToken is not null;
            _accessToken = null;
            _expiresAt = null;
            _user = null;
            _deviceId = null;
        }

        await RemoveQuietlyAsync(RefreshTokenKey, ct).ConfigureAwait(false);
        if (reasonCode == ApiErrorCodes.DeviceRevoked)
        {
            await ClearDeviceIdentityAsync(ct).ConfigureAwait(false);
        }

        var reason = reasonCode switch
        {
            null => SignOutReason.UserRequested,
            ApiErrorCodes.DeviceRevoked => SignOutReason.DeviceRevoked,
            ApiErrorCodes.AccountDisabled => SignOutReason.AccountDisabled,
            _ => SignOutReason.SessionExpired,
        };

        if (wasSignedIn)
        {
            RaiseChanged();
            SignedOut?.Invoke(this, new SignedOutEventArgs(reason));
        }
    }

    private async Task ClearDeviceIdentityAsync(CancellationToken ct)
    {
        await RemoveQuietlyAsync(DeviceIdKey, ct).ConfigureAwait(false);
        await RemoveQuietlyAsync(DeviceSecretKey, ct).ConfigureAwait(false);
    }

    private async Task RemoveQuietlyAsync(string key, CancellationToken ct)
    {
        try
        {
            await _secrets.RemoveAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove secret {Key}", key);
        }
    }

    private async Task<Guid?> GetStoredDeviceIdAsync(CancellationToken ct)
    {
        var raw = await _secrets.GetAsync(DeviceIdKey, ct).ConfigureAwait(false);
        if (Guid.TryParse(raw, out var id))
        {
            return id;
        }

        if (raw is not null)
        {
            _logger.LogWarning("Stored device id is not a GUID; ignoring it");
        }

        return null;
    }

    /// <summary>Trims and cuts to the server's maximum; null for blank input.</summary>
    internal static string? Clamp(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
