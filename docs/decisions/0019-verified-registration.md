# ADR-0019: verified registration, password reset, and outgoing mail and SMS

**Status:** accepted on 2026-11-06. Changes the registration modes of
[ADR-0017](0017-self-registration-with-approval.md) (adds one; `approval` stays the default) and point 6 of it ("no
email verification for now"). Phase 1 of the hosted service.

## Context

A hosted service cannot have a person approve every sign-up, and `open` mode lets anyone register any address. The
plan for the service calls for email plus a phone code with instant activation. The server could not send anything
until now, so there was no "forgot password" either.

## Decision

1. **A fourth `registration_mode`, `verify`.** Anyone may register; the account is created, active, once the person has
   entered a six-digit code mailed to the address - and, with the `require_phone` setting, a second one sent by SMS.
2. **No account until the codes are entered.** `POST /auth/register` stores a `pending_signups` row (the password is
   hashed then) and answers `202 {"status":"verify"}` whether or not the address has an account. An address that is
   taken gets no code; its owner is mailed a notice instead (at most once an hour). Signing in before the account exists
   is `invalid_credentials`, as for any wrong password. This closes the residual enumeration that `approval` mode has
   (where a fresh registration then signs in as `account_pending`).
3. **Codes**: six random digits, stored as an HMAC keyed with the server's secret, valid 15 minutes, dead after five
   wrong tries, resent at most once a minute and five times per registration, which itself expires after an hour. Wrong
   tries are committed before the error is returned. A per-IP budget (`VERIFY_RATE_LIMIT_PER_HOUR`, 30) covers every
   code endpoint.
4. **Email before phone.** The SMS step needs the `signup_token` handed out when the email is proved, so nobody can
   make the server text arbitrary numbers without a real mailbox first (SMS costs money and is a known abuse target).
   One account per phone number.
5. **Password reset** (`POST /auth/password/forgot`, `POST /auth/password/reset`) in every mode, whenever the server
   sends mail: the same silence for unknown addresses, a 15-minute code, and success revokes every refresh token and
   dashboard session and lifts a lockout.
6. **Senders are configuration, not settings**: `MAIL_BACKEND` = `none` | `console` | `smtp` (any provider that speaks
   SMTP: Postmark, SES, Resend…), `SMS_BACKEND` = `none` | `console` | `twilio` (the Messages API over HTTPS, no SDK).
   `console` writes codes to the log and is refused in `prod`. Messages are queued by the services and sent only
   after the commit; a failed send is logged, never surfaced, because every endpoint answers the same either way.
   `verify` mode without a mail sender, or `require_phone` without an SMS sender, answers `503`.

## Consequences

- The hosted service runs `registration_mode = verify`, `require_phone = true`, `workspace_mode = multi`
  ([ADR-0018](0018-workspaces.md)). Self-hosted servers keep `approval` and `single` unless their operator changes
  them.
- Accounts created by a verified registration carry `email_verified_at` (and `phone`, `phone_verified_at`); accounts
  made any other way do not, and are not asked to verify after the fact.
- The apps need the new screens (code entry, phone, forgot password) - Phase 3.
