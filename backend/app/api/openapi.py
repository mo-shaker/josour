"""OpenAPI presentation: the API description, the tag list, and per-endpoint error responses.

``/docs`` is the executable source of the REST contract (docs/api.md says so), and in production
it is admin-gated rather than absent - so it is what an operator and the client developer
actually read. That only works if the generated document carries what the hand-written contract
carries: what each endpoint is for, what it answers with, and which error codes it can produce.

``tests/test_openapi.py`` fails the build when a route is added without that, so this cannot rot.
"""

from typing import Any

from app.schemas.common import ErrorEnvelope

API_DESCRIPTION = """\
Control plane for Josour: accounts and devices, the shared domain allow-list, host
availability, and the lifecycle of a connection session. **No browsing traffic passes through
this API** - the tunnel is established directly between the two clients and the server only
coordinates it.

### Authentication
`Authorization: Bearer <access_token>` from `POST /auth/login`. Access tokens live 15 minutes;
refresh tokens live 30 days and are rotated on every use (presenting a rotated token revokes the
whole device chain).

### Errors
Every failure is `{"error": {"code": "...", "message": "..."}}` with a matching HTTP status. The
codes are stable: `invalid_credentials`, `account_locked`, `account_disabled`, `device_revoked`,
`unauthorized`, `forbidden`, `not_found`, `validation_error`, `rate_limited`, `conflict`.

### Rate limits
`POST /auth/login` is limited per client IP *and* per submitted email, `POST /auth/refresh` per
client IP, and `POST /probe` per user. Exceeding one answers `429 rate_limited` with a
`Retry-After` header in seconds. The policy and its numbers are ADR-0008.

### Real-time
Everything time-sensitive (presence, connection requests, session lifecycle) travels over the
`/ws` WebSocket control channel, which is specified in `docs/ws-protocol.md` and is not part of
this document.
"""

TAGS_METADATA: list[dict[str, Any]] = [
    {
        "name": "auth",
        "description": (
            "Sign in, rotate a refresh token, sign out. `POST /auth/login` both authenticates "
            "the user and registers or verifies the calling device; the device secret is "
            "returned exactly once, at registration."
        ),
    },
    {
        "name": "me",
        "description": "The calling user and the devices they have registered.",
    },
    {
        "name": "hosts",
        "description": (
            "Devices currently advertising themselves as available hosts - the same rows, "
            "filter and order as the `hosts.snapshot` WebSocket frame."
        ),
    },
    {
        "name": "sessions",
        "description": (
            "The caller's own session history. Sessions are created and ended over the "
            "WebSocket; this tag is read-only."
        ),
    },
    {
        "name": "domains",
        "description": (
            "The centrally managed allow-list as an immutable, versioned snapshot. A host reads "
            "the exact version a pending request will apply before accepting it."
        ),
    },
    {
        "name": "probe",
        "description": (
            "Reachability check used by the connectivity model. The server opens a TCP "
            "connection to a public address and closes it immediately; it never sends a byte, "
            "and private, loopback and reserved targets are refused."
        ),
    },
    {
        "name": "diagnostics",
        "description": (
            "Client-reported connection diagnostics feeding the relay decision gate. Never "
            "carries browsing content."
        ),
    },
    {
        "name": "admin",
        "description": (
            "Operator API; every route requires `role = admin`. In production `/docs` and "
            "`/openapi.json` sit behind this same check."
        ),
    },
]

COMMON_ERRORS: dict[int, str] = {
    400: "`validation_error` - the request was well-formed but the value is not allowed here.",
    401: "`unauthorized` - missing, malformed or expired access token.",
    403: "`forbidden` - authenticated but not allowed. `device_revoked` when the calling device "
    "has been revoked.",
    404: "`not_found` - no such resource, or not one this caller may see.",
    409: "`conflict` - the resource is not in a state that allows this.",
    422: "`validation_error` - the body or query parameters failed validation.",
    429: "`rate_limited` - rate limit exceeded. Retry after the `Retry-After` header (seconds).",
}


def errors(*codes: int, custom: dict[int, str] | None = None) -> dict[int | str, dict[str, Any]]:
    """The ``responses=`` block for a route: every documented failure, rendered as the shared
    ``{"error": {...}}`` envelope so the generated schema matches what the handlers emit."""
    described = {code: COMMON_ERRORS[code] for code in codes}
    described.update(custom or {})
    return {
        code: {"model": ErrorEnvelope, "description": description}
        for code, description in sorted(described.items())
    }
