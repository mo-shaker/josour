namespace Josour.Core.Session;

/// <summary>
/// One standing decision by the host: "requests from this guest, on this device, may be accepted without asking me".
/// docs/ws-protocol.md section 5a.
/// <para>
/// The key is the <see cref="GuestUserId"/>/<see cref="GuestDeviceId"/> pair, never the display names. Names are chosen
/// by the guest, may repeat between users and change at any time, so a rule written against one would hand a decision
/// made about somebody to a renamed stranger. The names are kept anyway, but only so the host can recognise its own
/// rules in the settings list.
/// </para>
/// </summary>
/// <param name="GuestUserId">The guest's user id, as <c>request.incoming.guest_user_id</c>.</param>
/// <param name="GuestDeviceId">The guest's device id. A reinstall gives the guest a new one and it is asked about
/// again — deliberately, the way <c>known_hosts</c> does.</param>
/// <param name="GuestName">Display name at the time the rule was written; for the settings list only.</param>
/// <param name="GuestDevice">Device name at the time the rule was written; for the settings list only.</param>
/// <param name="MaxDurationMinutes">Longest session this rule will accept unasked. A host that agreed to half an hour
/// did not agree to a working day.</param>
/// <param name="ExpiresAt">When the rule stops applying; <c>null</c> means it does not expire on its own.</param>
/// <param name="AllowlistVersion">The allow-list version in force when the rule was written. Only consulted where the
/// deployment enforces the list (ADR-0010): if it has moved on, what a request grants is no longer what was agreed to.</param>
/// <param name="GrantedAt">When the host wrote the rule; shown in settings so an old decision looks old.</param>
public sealed record TrustedGuest(
    Guid GuestUserId,
    Guid GuestDeviceId,
    string GuestName,
    string GuestDevice,
    int MaxDurationMinutes,
    DateTimeOffset? ExpiresAt,
    int AllowlistVersion,
    DateTimeOffset GrantedAt)
{
    /// <summary>An identity that could not have come from a server that sends the field (section 5a).</summary>
    public bool HasUsableIdentity => GuestUserId != Guid.Empty && GuestDeviceId != Guid.Empty;

    public bool Matches(Guid guestUserId, Guid guestDeviceId) =>
        HasUsableIdentity && GuestUserId == guestUserId && GuestDeviceId == guestDeviceId;

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;
}

/// <summary>
/// The host's auto-accept configuration: the master switch and the rules under it. Persisted as one file so the switch
/// and the rules are always read and written together — a switch that could be on while the rules were still loading
/// would be a switch with no meaning.
/// </summary>
public sealed record AutoAcceptSettings
{
    /// <summary>Off. Auto-accept is never the shipped state: it is a thing a host turns on knowing what it is.</summary>
    public static AutoAcceptSettings Default { get; } = new();

    /// <summary>The master switch. Turning it off stops every rule at once without deleting any of them.</summary>
    public bool Enabled { get; init; }

    public IReadOnlyList<TrustedGuest> Guests { get; init; } = Array.Empty<TrustedGuest>();

    /// <summary>The rule for this guest, if the host wrote one. Identity-keyed; never name-keyed.</summary>
    public TrustedGuest? Find(Guid guestUserId, Guid guestDeviceId) =>
        guestUserId == Guid.Empty || guestDeviceId == Guid.Empty
            ? null
            : Guests.FirstOrDefault(g => g.Matches(guestUserId, guestDeviceId));

    /// <summary>Adds the rule, replacing any earlier one for the same guest and device.</summary>
    public AutoAcceptSettings With(TrustedGuest guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        if (!guest.HasUsableIdentity)
        {
            throw new ArgumentException("A trusted guest must carry a real user id and device id", nameof(guest));
        }

        return this with
        {
            Guests = Guests
                .Where(g => !g.Matches(guest.GuestUserId, guest.GuestDeviceId))
                .Append(guest)
                .ToArray(),
        };
    }

    public AutoAcceptSettings Without(Guid guestUserId, Guid guestDeviceId) => this with
    {
        Guests = Guests.Where(g => !g.Matches(guestUserId, guestDeviceId)).ToArray(),
    };

    /// <summary>Drops rules that have run out. Housekeeping only: <see cref="AutoAcceptPolicy"/> checks expiry itself,
    /// so a list that was never swept is still safe — just untidy.</summary>
    public AutoAcceptSettings WithoutExpired(DateTimeOffset now) => this with
    {
        Guests = Guests.Where(g => !g.IsExpiredAt(now)).ToArray(),
    };
}
