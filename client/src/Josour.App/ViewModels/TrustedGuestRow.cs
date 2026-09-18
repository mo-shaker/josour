using Josour.Core.Session;

namespace Josour.App.ViewModels;

/// <summary>
/// One trusted guest as the settings list shows it (docs/ws-protocol.md section 5a): who, on which device, the longest
/// session the rule accepts, and when the rule stops. The identity is carried along so Remove can address the right
/// rule — the names are not unique and are not what the rule is keyed on.
/// </summary>
public sealed record TrustedGuestRow(
    Guid GuestUserId,
    Guid GuestDeviceId,
    string GuestName,
    string GuestDevice,
    string LimitText,
    string ExpiryText)
{
    public static TrustedGuestRow From(TrustedGuest guest, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(guest);
        return new TrustedGuestRow(
            guest.GuestUserId,
            guest.GuestDeviceId,
            guest.GuestName,
            UiFlow.Ltr(guest.GuestDevice),
            string.Format(UiFlow.Culture, Strings.SettingsAutoAcceptMinutesFormat, guest.MaxDurationMinutes),
            Expiry(guest, now));
    }

    /// <summary>
    /// An expired rule is listed as expired rather than hidden. It no longer grants anything — the policy checks expiry
    /// itself — but a host looking for "who did I trust" should see what it once decided, and be able to clear it.
    /// </summary>
    private static string Expiry(TrustedGuest guest, DateTimeOffset now) => guest.ExpiresAt switch
    {
        null => Strings.SettingsAutoAcceptNeverExpires,
        { } expiry when expiry <= now => Strings.SettingsAutoAcceptExpired,
        { } expiry => UiFlow.Ltr(expiry.ToLocalTime().ToString("yyyy-MM-dd", UiFlow.Culture)),
    };
}
