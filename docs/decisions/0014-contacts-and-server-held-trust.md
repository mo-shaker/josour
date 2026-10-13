# ADR-0014: contacts, offers in both directions, and trust held by the server

**Status:** accepted on 2026-10-13. Adds [section 10](../ws-protocol.md#10-contacts-offers-and-server-held-trust-added-2026-10-13)
to the WebSocket contract and the "Contacts" section to the [REST contract](../api.md). Supersedes the storage and keying
of [ADR-0012](0012-auto-accept-trusted-guests.md) (its decisions 1–5 and 8); keeps its decisions 6–7.

## Context

Until now every signed-in user saw every available host on the server: the only gate was the host pressing "Accept".
That fits a deployment of three people who all know each other and nothing larger, and it is the wrong default for a
tool whose whole effect is that strangers' traffic leaves from your home. The owner asked for a different shape:

- people add each other **by email**, and only once the other side accepts can the two share a connection;
- either side can start: the one who needs a connection asks for it, **or** the one who has it offers it;
- a person can mark a contact as "may use my connection at any time without asking me";
- either side can cut the session at any moment.

ADR-0012 already allowed a host to answer without a window, but kept the rule on the host's own disk, keyed on one
device of one guest. That cannot survive contacts: the rule belongs to a *person* and must work from every device the
host signs in from, and the server must be able to show and withdraw it.

## Decision

1. **Contacts are a server relationship, created by invitation and acceptance.** An invitation is addressed to an
   email; accepting writes two rows, `(A, B)` and `(B, A)`. Removing deletes both, settles anything pending between the
   two and ends a session between them (`contact_removed`).
2. **Invitations never reveal who has an account.** `POST /contacts/invites` answers `202 {}` whatever happened; the
   inviter's list shows `pending` until acceptance or expiry; a decline is silent. An address without an account gets
   the invitation if it registers and is approved in time.
3. **Only contacts see each other.** The host list (`GET /hosts`, `hosts.snapshot`, `hosts.update`) is filtered by one
   query in `app.services.hosts`; `request.create` and `offer.create` to a non-contact answer `not_contact`.
4. **Offers.** `offer.create` lets a host offer its connection; it rings every `offers`-capable device of the guest and
   the first to accept becomes the session's guest device. The guest always has to accept: taking an offer points the
   guest's own machine at somebody else's address.
5. **Trust is stored on the server, per contact, and owned by the connection's owner.** `(A, B).auto_accept` — "A lets
   B use A's connection without asking" — is set only by A (`PUT /contacts/{B}/trust`). An administrator can withdraw a
   grant, never give one.
6. **The host's device still performs the acceptance.** The server marks the request `trusted`; a `trust_v2` client
   answers `request.accept {auto: true}` without a window; the server re-checks the grant in the same transaction and
   answers `trust_revoked` if it was withdrawn meanwhile. Every session is still accepted by the host's own device —
   which has to be online to run the tunnel anyway.
7. **Inform, do not ask** (from ADR-0012): an auto-accepted session produces a non-blocking notification with "end
   session" and "stop allowing", and a `request_auto_accepted` audit row, now with `grant: server`.
8. **Additive protocol.** Clients declare `hello.capabilities`; the server sends only what was declared. Old clients keep
   working against the new server (they see only contacts), and `manage.py seed-contacts --all-active` links every pair
   of existing accounts once on upgrade so nobody loses a host they could use before.

## Reasons

- The server is the only place that can make "who may see whom" true everywhere at once — on every device and in the
  admin dashboard — and it already knows both identities and holds the session secret ([ADR-0009](0009-relay-default.md)).
- Keeping the final acceptance on the host's device preserves the property ADR-0012 cared about: no session starts
  without the host's own machine saying yes, now or in advance.
- Addressing invitations to emails, not accounts, is what makes "same answer either way" possible without lying.

## Consequences

- A compromised server could now *mark* a request trusted. It still cannot make a host's device accept one the host
  did not trust: a `trust_v2` client cross-checks against the trust it fetched from `GET /contacts`, and the server was
  already inside the trust boundary for session secrets.
- The local `auto-accept.json` of ADR-0012 is retired by the 2.0 client; trust must be granted again per contact.
- The global host list is gone. An operator upgrading must run `seed-contacts` or tell users to invite each other.
