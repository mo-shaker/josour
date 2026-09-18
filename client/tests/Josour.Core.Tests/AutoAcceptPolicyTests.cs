using Josour.Core.Session;

namespace Josour.Core.Tests;

/// <summary>
/// docs/ws-protocol.md section 5a. This is the one place in the product where consent is given by a machine instead of
/// by a person, so the tests are written the pessimistic way round: each one names a condition and proves that failing
/// it produces a prompt, not a session.
/// </summary>
public sealed class AutoAcceptPolicyTests
{
    private static readonly Guid GuestUser = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid GuestDevice = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static TrustedGuest Rule(
        int maxMinutes = 60,
        DateTimeOffset? expiresAt = null,
        int allowlistVersion = 3,
        Guid? userId = null,
        Guid? deviceId = null) =>
        new(
            userId ?? GuestUser,
            deviceId ?? GuestDevice,
            "Sara Ahmed",
            "SARA-LAPTOP",
            maxMinutes,
            expiresAt,
            allowlistVersion,
            Now.AddDays(-1));

    private static AutoAcceptSettings Enabled(params TrustedGuest[] guests) =>
        new() { Enabled = true, Guests = guests };

    private static AutoAcceptVerdict Evaluate(
        AutoAcceptSettings settings,
        int durationMinutes = 30,
        int allowlistVersion = 3,
        bool enforceAllowlist = false,
        Guid? userId = null,
        Guid? deviceId = null,
        DateTimeOffset? now = null) =>
        AutoAcceptPolicy.Evaluate(
            settings,
            userId ?? GuestUser,
            deviceId ?? GuestDevice,
            durationMinutes,
            allowlistVersion,
            enforceAllowlist,
            now ?? Now);

    [Fact]
    public void A_trusted_guest_within_every_limit_is_accepted()
    {
        var verdict = Evaluate(Enabled(Rule()));

        Assert.Equal(AutoAcceptOutcome.Accept, verdict.Outcome);
        Assert.True(verdict.ShouldAccept);
        Assert.NotNull(verdict.Guest);
    }

    [Fact]
    public void The_shipped_state_prompts()
    {
        // Nothing configured at all: the feature must be something a host switched on, never something it inherited.
        var verdict = Evaluate(AutoAcceptSettings.Default);

        Assert.Equal(AutoAcceptOutcome.Disabled, verdict.Outcome);
        Assert.False(verdict.ShouldAccept);
    }

    [Fact]
    public void The_master_switch_stops_a_rule_without_deleting_it()
    {
        var settings = Enabled(Rule()) with { Enabled = false };

        var verdict = Evaluate(settings);

        Assert.Equal(AutoAcceptOutcome.Disabled, verdict.Outcome);
        Assert.Single(settings.Guests);
    }

    [Fact]
    public void A_guest_with_no_rule_prompts()
    {
        var verdict = Evaluate(Enabled(Rule()), userId: Guid.NewGuid());

        Assert.Equal(AutoAcceptOutcome.NotTrusted, verdict.Outcome);
    }

    [Fact]
    public void The_same_user_on_a_different_device_prompts()
    {
        // The device is half the key on purpose: a reinstall, or a second machine, is a new thing to be asked about.
        var verdict = Evaluate(Enabled(Rule()), deviceId: Guid.NewGuid());

        Assert.Equal(AutoAcceptOutcome.NotTrusted, verdict.Outcome);
    }

    [Fact]
    public void A_rule_that_has_run_out_prompts()
    {
        var verdict = Evaluate(Enabled(Rule(expiresAt: Now.AddSeconds(-1))));

        Assert.Equal(AutoAcceptOutcome.Expired, verdict.Outcome);
    }

    [Fact]
    public void A_rule_expiring_exactly_now_prompts()
    {
        // The boundary belongs to the safe side: "until noon" has stopped meaning anything at noon.
        var verdict = Evaluate(Enabled(Rule(expiresAt: Now)));

        Assert.Equal(AutoAcceptOutcome.Expired, verdict.Outcome);
    }

    [Fact]
    public void A_rule_with_no_expiry_keeps_applying()
    {
        var verdict = Evaluate(Enabled(Rule(expiresAt: null)), now: Now.AddYears(5));

        Assert.Equal(AutoAcceptOutcome.Accept, verdict.Outcome);
    }

    [Fact]
    public void A_request_longer_than_the_rule_allows_prompts()
    {
        var verdict = Evaluate(Enabled(Rule(maxMinutes: 30)), durationMinutes: 31);

        Assert.Equal(AutoAcceptOutcome.TooLong, verdict.Outcome);
    }

    [Fact]
    public void A_request_of_exactly_the_allowed_length_is_accepted()
    {
        var verdict = Evaluate(Enabled(Rule(maxMinutes: 30)), durationMinutes: 30);

        Assert.Equal(AutoAcceptOutcome.Accept, verdict.Outcome);
    }

    [Fact]
    public void A_moved_allow_list_prompts_where_the_list_is_enforced()
    {
        var verdict = Evaluate(Enabled(Rule(allowlistVersion: 3)), allowlistVersion: 4, enforceAllowlist: true);

        Assert.Equal(AutoAcceptOutcome.AllowlistChanged, verdict.Outcome);
    }

    [Fact]
    public void A_moved_allow_list_is_not_consulted_where_the_list_is_not_enforced()
    {
        // ADR-0010: with no restriction in force the version promises nothing, and refusing on it would refuse
        // every request on every deployment that ships the default.
        var verdict = Evaluate(Enabled(Rule(allowlistVersion: 3)), allowlistVersion: 4, enforceAllowlist: false);

        Assert.Equal(AutoAcceptOutcome.Accept, verdict.Outcome);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_server_that_does_not_name_the_guest_prompts(bool emptyUser, bool emptyDevice)
    {
        // A deployment older than section 5a sends no identity, and the absent field arrives as Guid.Empty. Matching
        // on it would turn one rule into a rule about everybody.
        var verdict = Evaluate(
            Enabled(Rule()),
            userId: emptyUser ? Guid.Empty : GuestUser,
            deviceId: emptyDevice ? Guid.Empty : GuestDevice);

        Assert.Equal(AutoAcceptOutcome.UnknownGuest, verdict.Outcome);
    }

    [Fact]
    public void A_rule_stored_against_an_empty_identity_can_never_match()
    {
        // Belt and braces: even if such a row reached the file, the lookup itself refuses it.
        var settings = new AutoAcceptSettings
        {
            Enabled = true,
            Guests = new[] { Rule(userId: Guid.Empty, deviceId: Guid.Empty) },
        };

        Assert.Null(settings.Find(Guid.Empty, Guid.Empty));
        Assert.Equal(AutoAcceptOutcome.UnknownGuest, Evaluate(settings, userId: Guid.Empty, deviceId: Guid.Empty).Outcome);
    }

    [Fact]
    public void Adding_a_rule_replaces_the_earlier_one_for_the_same_guest_and_device()
    {
        var settings = AutoAcceptSettings.Default.With(Rule(maxMinutes: 30)).With(Rule(maxMinutes: 90));

        var guest = Assert.Single(settings.Guests);
        Assert.Equal(90, guest.MaxDurationMinutes);
    }

    [Fact]
    public void A_rule_with_no_identity_cannot_be_added()
    {
        Assert.Throws<ArgumentException>(() => AutoAcceptSettings.Default.With(Rule(userId: Guid.Empty)));
    }

    [Fact]
    public void Removing_a_rule_leaves_the_other_guests_alone()
    {
        var other = Rule(userId: Guid.NewGuid(), deviceId: Guid.NewGuid());
        var settings = AutoAcceptSettings.Default.With(Rule()).With(other);

        var after = settings.Without(GuestUser, GuestDevice);

        var left = Assert.Single(after.Guests);
        Assert.Equal(other.GuestUserId, left.GuestUserId);
    }

    [Fact]
    public void Sweeping_drops_only_what_has_run_out()
    {
        var live = Rule(expiresAt: Now.AddDays(1));
        var dead = Rule(userId: Guid.NewGuid(), deviceId: Guid.NewGuid(), expiresAt: Now.AddDays(-1));
        var settings = AutoAcceptSettings.Default.With(live).With(dead);

        var after = settings.WithoutExpired(Now);

        var left = Assert.Single(after.Guests);
        Assert.Equal(live.GuestUserId, left.GuestUserId);
    }
}
