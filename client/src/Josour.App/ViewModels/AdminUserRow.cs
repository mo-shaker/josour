using Josour.Infrastructure.Api;

namespace Josour.App.ViewModels;

/// <summary>
/// One account as the admin panel lists it: the identity, and the three states an administrator acts on.
/// <para>
/// The states are kept apart on purpose. "Disabled" is a decision somebody took; "locked out" is what repeated
/// failed sign-ins did on their own (ADR-0008) and clears itself; and the two call for different buttons. A single
/// "blocked" badge covering both would make an administrator unlock an account nobody locked, or wait out a lockout
/// that was really a decision.
/// </para>
/// </summary>
public sealed record AdminUserRow(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool IsLocked,
    int FailedLogins,
    bool IsSelf)
{
    public static AdminUserRow From(AdminUserDto user, Guid? currentUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new AdminUserRow(
            user.Id,
            UiFlow.Ltr(user.Email),
            user.DisplayName,
            user.Role,
            user.IsActive,
            user.IsLockedAt(now),
            user.FailedLogins,
            IsSelf: currentUserId is { } me && me == user.Id);
    }

    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the state column reads: one sentence, never a colour on its own.</summary>
    public string StateText => (IsActive, IsLocked) switch
    {
        (false, _) => Strings.AdminStateDisabled,
        (true, true) => Strings.AdminStateLocked,
        (true, false) => Strings.AdminStateActive,
    };

    /// <summary>
    /// An administrator may not disable their own account. Not a safety rail against a mistake — it is the only
    /// action in the panel that can lock the last administrator out of the server with no way back in except the
    /// command line.
    /// </summary>
    public bool CanToggleActive => !IsSelf;

    public bool CanUnlock => IsLocked;

    public string ToggleActiveText => IsActive ? Strings.AdminDisable : Strings.AdminEnable;
}
