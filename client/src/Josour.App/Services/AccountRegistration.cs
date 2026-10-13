using Josour.Infrastructure.Api;

namespace Josour.App.Services;

/// <summary>What became of a registration from the app (ADR-0017).</summary>
public enum RegistrationResult
{
    /// <summary>The account is active: sign in with the same email and password now.</summary>
    SignInNow,

    /// <summary>The account exists but an administrator has to approve it first.</summary>
    Pending,

    Failed,
}

/// <summary>
/// <c>POST /auth/register</c> for the sign-in window and the first run alike, so both say the same thing about every
/// outcome. A registration never signs in by itself: on <see cref="RegistrationResult.SignInNow"/> the caller signs in
/// through the normal path, which is also what registers this device.
/// </summary>
public static class AccountRegistration
{
    public const int MinimumPasswordLength = 8;

    public static async Task<(RegistrationResult Result, string? Message)> RegisterAsync(
        IApiClient api, string email, string password, string displayName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (RegistrationResult.Failed, Strings.RegisterNameRequired);
        }

        if (password.Length < MinimumPasswordLength)
        {
            return (RegistrationResult.Failed, Strings.RegisterPasswordHint);
        }

        try
        {
            var response = await api.RegisterAsync(email.Trim(), password, displayName.Trim(), ct).ConfigureAwait(true);
            return response.IsActive
                ? (RegistrationResult.SignInNow, null)
                : (RegistrationResult.Pending, Strings.RegisterPendingText);
        }
        catch (ApiException ex)
        {
            return (RegistrationResult.Failed, MapError(ex));
        }
        catch (ApiUnavailableException)
        {
            return (RegistrationResult.Failed, Strings.LoginErrorUnavailable);
        }
    }

    public static string MapError(ApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Code switch
        {
            ApiErrorCodes.RegistrationClosed => Strings.RegisterClosed,
            ApiErrorCodes.RegistrationUnavailable => Strings.RegisterUnavailable,
            ApiErrorCodes.Conflict => Strings.RegisterEmailTaken,
            ApiErrorCodes.RateLimited => Strings.LoginErrorRateLimited,
            ApiErrorCodes.UpgradeRequired => Strings.ErrorUpgradeRequired,
            ApiErrorCodes.ValidationError => string.Format(UiFlow.Culture, Strings.LoginErrorValidationFormat, ex.Message),
            _ => string.Format(UiFlow.Culture, Strings.LoginErrorGenericFormat, ex.Code),
        };
    }
}
