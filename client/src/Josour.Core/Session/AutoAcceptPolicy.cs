namespace Josour.Core.Session;

/// <summary>Why a request was not accepted without asking. Every value but <see cref="Accept"/> means "show the prompt".</summary>
public enum AutoAcceptOutcome
{
    /// <summary>Every condition in docs/ws-protocol.md section 5a held: answer <c>request.accept</c> with <c>auto: true</c>.</summary>
    Accept,

    /// <summary>The master switch is off — including the shipped state, where it always is.</summary>
    Disabled,

    /// <summary>The server did not name the guest (a deployment older than section 5a), so there is nothing to match on.</summary>
    UnknownGuest,

    /// <summary>The host never wrote a rule for this guest on this device.</summary>
    NotTrusted,

    /// <summary>There is a rule, but it has run out.</summary>
    Expired,

    /// <summary>The request asks for longer than the rule allows.</summary>
    TooLong,

    /// <summary>The allow-list moved since the rule was written, so the request no longer grants what was agreed to.</summary>
    AllowlistChanged,
}

/// <summary>The decision, and the rule it was taken against when there was one.</summary>
public readonly record struct AutoAcceptVerdict(AutoAcceptOutcome Outcome, TrustedGuest? Guest)
{
    public bool ShouldAccept => Outcome == AutoAcceptOutcome.Accept;
}

/// <summary>
/// Decides whether an incoming request may be accepted without showing the host anything (docs/ws-protocol.md section 5a).
/// <para>
/// Pure and total: same inputs, same answer, no clock and no I/O of its own. That is the point — this is the one place in
/// the product where the host's consent is given by a machine rather than by a person, so it has to be the piece that can
/// be read in full and tested exhaustively. Everything it does not explicitly allow, it refuses.
/// </para>
/// </summary>
public static class AutoAcceptPolicy
{
    /// <param name="settings">The host's switch and rules.</param>
    /// <param name="guestUserId">From <c>request.incoming.guest_user_id</c>.</param>
    /// <param name="guestDeviceId">From <c>request.incoming.guest_device_id</c>.</param>
    /// <param name="durationMinutes">What the guest asked for.</param>
    /// <param name="allowlistVersion">The request's own allow-list version.</param>
    /// <param name="enforceAllowlist">Whether the deployment restricts sites at all (ADR-0010). When it does not, the
    /// version is not a promise about anything and comparing it would refuse every request for no reason.</param>
    /// <param name="now">The moment to judge expiry against.</param>
    public static AutoAcceptVerdict Evaluate(
        AutoAcceptSettings settings,
        Guid guestUserId,
        Guid guestDeviceId,
        int durationMinutes,
        int allowlistVersion,
        bool enforceAllowlist,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.Disabled, null);
        }

        // A server that predates section 5a sends no identity, and the absent field deserializes to Guid.Empty.
        // Matching on it would make one rule written against "nothing" accept every guest on that deployment.
        if (guestUserId == Guid.Empty || guestDeviceId == Guid.Empty)
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.UnknownGuest, null);
        }

        var guest = settings.Find(guestUserId, guestDeviceId);
        if (guest is null)
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.NotTrusted, null);
        }

        if (guest.IsExpiredAt(now))
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.Expired, guest);
        }

        if (durationMinutes > guest.MaxDurationMinutes)
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.TooLong, guest);
        }

        if (enforceAllowlist && allowlistVersion != guest.AllowlistVersion)
        {
            return new AutoAcceptVerdict(AutoAcceptOutcome.AllowlistChanged, guest);
        }

        return new AutoAcceptVerdict(AutoAcceptOutcome.Accept, guest);
    }
}
