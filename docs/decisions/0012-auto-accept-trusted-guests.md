# ADR-0012: auto-accepting guests the host trusts

**Status:** proposed on 2026-09-18. Adds [section 5a](../ws-protocol.md#5a-auto-accepting-a-trusted-guest) to the
WebSocket contract. Supersedes nothing.

## Context

Until today every session begins with a window the host reads and presses "Accept" in. That is right for the first
session, and wearing in the situation the product actually lives in: **the same two people, every day**. A host
sharing their connection with their brother or their colleague does not want to be asked the same question about the
same person twelve times a week, and worse than the tedium is its effect: a question asked daily with the same answer
gets answered without being read, and the window becomes a ritual rather than a consent. And the host may be away from
their machine, so the request is missed and the guest gets nothing.

The feature was requested as "approval happens automatically without the host intervening". That wording admits two
far-apart meanings, and the difference between them is everything:

- **Someone approving on the host's behalf** — the server or an administrator. That abolishes the consent rather than
  deferring it.
- **The host approving in advance**, their decision being carried out later without them present. The consent here is
  complete; what changed is the moment it was given.

## Decision

**The second meaning, and under all of the following conditions together.**

1. **The decision is in the host's client alone.** The server stores no trust lists and approves on nobody's behalf.
   The host's client is what matches the rule and answers `request.accept` with `auto: true`.
2. **The rule's key is `(guest_user_id, guest_device_id)`**, not the display name. That is why the two fields were
   added to `request.incoming` and `peer`.
3. **A master switch, off by default**, whose switching off stops every rule at once without deleting them.
4. **A duration ceiling per rule**, defaulting to the duration of the request at which the host granted trust.
5. **An expiry per rule**: a week (the default), a month, or forever.
6. **Inform, do not ask**: a non-blocking notification on every automatic acceptance, and ending the session is
   available at any moment.
7. **A `request_auto_accepted` security event** on the server, attributed to the host.
8. **Trust is granted from the request window alone** — the one screen where the host sees who is asking — and
   withdrawn from the settings.

## Reasons

**Why the client and not the server?** Because "automatic" here means the host's application answers instead of their
person, not that the server answers instead of their machine. And because a host disconnected from `/ws` is outside
`hosts.*` to begin with, so no request reaches them — acceptance by the server would not add a case where it works
where it otherwise would not; it would only add a third party holding a decision that is not theirs. For the same
reason the "a policy the administrator sets" option was rejected: whoever shares their address decides who with.

**Why the identity and not the name?** `guest_name` is chosen by the guest, changed whenever they like, and may repeat
between users. A rule written on a name bequeaths a decision taken about one person to a stranger who renamed
themselves, and that is a silent security defect, because all the host sees at that point is the name they trusted.

**Why is the device part of the key?** So that compromising the guest's account — or their registering a new device —
does not grant automatic access. A guest who reinstalls is asked about once more, exactly as `known_hosts` does, and
that is a small price for what it buys.

**Why a duration ceiling?** Because someone who agreed to half an hour did not agree to a working day. The rule is
written at a request with a definite duration, so it is reasonable for that number to be its ceiling.

**Why a default expiry?** Trust that ends by itself is trust whose review can be forgotten harmlessly. "Forever" is
available to whoever wants it, but it is not what the field does if left alone.

**Why the notification?** Because a host who learns their connection was used only from a bill or from slowness was not
told, and being told is the part of consent that automatic acceptance may not spend. The notification is
non-blocking: it informs and does not ask.

**Why the security event?** Because this is the one consent no human witnessed as it happened, so it is the consent
most in need of being on the record. It revokes nothing and drops no device ([ADR-0008](0008-rate-limit-policy.md)) —
it is a trace to be reviewed, not an action to be taken.

## Consequences

- **The host takes on a wider responsibility knowingly.** After [ADR-0010](0010-route-all-through-host.md) a session
  means "any site, with the host's address", and automatic acceptance means that **with no window each time**. That is
  why the settings show an explicit warning for as long as the switch is on, and why trust is granted from the
  disclosure window alone.
- **`enforce_allowlist` constrains the rule.** When the list is in force, an `allowlist_version` that differs from the
  one recorded when trust was granted asks again: what the request grants is no longer what the host agreed to. Where
  the list is not enforced (the default, ADR-0010) the comparison is meaningless and is not made.
- **A server older than this decision does not send the identity**, so `Guid.Empty` arrives. The policy explicitly
  refuses to match on it, otherwise one rule would become a rule about everybody. This is tested.
- **The `auto-accept.json` file is a record of consent**, so a failure to read it is read as "off" and logged as an
  error. Failing in that direction is annoying; failing in the other is catastrophic.
- **The switch and the trust list apply the moment they change, not on save.** A host who turned the switch off and
  then closed the window must not still be accepting automatically.
- **`request.accept` now carries a new field defaulting to `false`**, so an older client stays correct and is never
  recorded as having skipped a window it did show.
