# ADR-0016: an administration dashboard on the web, served by the API itself

**Status:** accepted on 2026-10-13 (implemented in `backend/app/web`). Reverses the "no web panel" stance of the README and of plan section 14.

## Context

Administration lived in the app (`role = admin`) and the shell, deliberately: "an administrative panel on the internet
would be the highest-value target in the system". The owner now wants full control of users from a browser on the
server itself — approving registrations (ADR-0017), deleting accounts, seeing and cutting contacts and sessions.

## Decision

1. **Server-rendered pages at `/admin` in the same FastAPI process.** It must be the same process: the WebSocket
   registry, the timers and the event bus are in memory (single worker), and "terminate this session" has to reach the
   live sockets. Jinja2 templates, plain HTML forms, **no JavaScript** (`script-src 'none'`), Arabic (RTL) and English.
2. **A separate browser session**, not the app's device-bound tokens: a server-side `admin_web_sessions` row behind a
   `__Host-` cookie (HttpOnly, Secure, SameSite=Strict), 30 minutes idle and 12 hours absolute, revoked on password
   change, deactivation, demotion and deletion.
3. **CSRF**: a synchronizer token stored in the session row on every form, plus an `Origin` check.
4. **Optional TOTP** second factor per administrator, and a switch to require it.
5. **Optional network allowlist** (`ADMIN_WEB_ALLOWED_CIDRS`): outside it the dashboard answers `404`.
6. **The same services as the admin API**: every action goes through `app.services` and its after-commit effects, so a
   button in the browser and a call to the API cannot differ; every action is audited with `via: "web"`.
7. **Hardening at the edge**: `Content-Security-Policy` with `frame-ancestors 'none'`, `Cache-Control: no-store`, and
   no response compression on `/admin` (BREACH).

## Consequences

- A new internet-facing surface. The mitigations above are the price of the feature; an operator who does not want it
  sets `ADMIN_WEB_ENABLED=false` or restricts it with the CIDR allowlist or at Caddy.
- New dependencies: `jinja2` and `python-multipart`. Templates and static files must be packaged (`package-data`).
