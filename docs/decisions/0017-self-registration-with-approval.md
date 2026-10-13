# ADR-0017: self-registration from the app, with administrator approval by default

**Status:** accepted on 2026-10-13. Adds "Self-registration" to the [REST contract](../api.md).

## Context

Accounts were created only by an administrator, from the shell or the in-app panel. The owner asked for people to be
able to register from the app. Unrestricted sign-up does not fit this product: every account is a potential guest
behind somebody's home address, and the operator answers for who is on the server.

## Decision

1. **A `registration_mode` operator setting**: `closed`, `approval` (the default) or `open`. `GET /auth/registration`
   tells the app which, so it can hide "Create account" when closed.
2. **`approval` creates the account `pending`.** It exists and its email is taken, but it cannot sign in until an
   administrator approves it (dashboard, admin API or `manage.py approve-user`). It can also be rejected; a rejected
   account stays, so the address cannot simply register again, and can still be approved later.
3. **`approval_status` beside `is_active`, tied by a CHECK** (`approval_status = 'approved' OR is_active = false`), so
   every existing `is_active` gate keeps a pending account out without having to know approval exists.
4. **No account enumeration.** In `approval` mode the answer is `202 {"status":"pending"}` whether or not the address
   had an account; the attempt on a taken address is a `registration_duplicate` audit row. `account_pending` and
   `account_rejected` are only ever answered to a correct password. `open` mode cannot hide a taken address (`409`), and
   says so.
5. **Bounds.** 5 registrations per hour per IP, and at most `MAX_PENDING_REGISTRATIONS` (200) waiting accounts.
6. **No email verification for now.** There is no SMTP in the deployment; approval by a person is the check. Adding it
   later is additive.
7. **Soft delete.** An administrator can delete an account; the row is anonymised rather than removed, because
   `sessions.*_user_id` cascade and a hard delete would erase the other party's history.

## Consequences

- An approval queue an operator has to look at (the dashboard shows it on its first page).
- A registration does not register a device: the app signs in with `POST /auth/login` once the account may be used, the
  same path as every other account.
