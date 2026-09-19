"""String enums persisted as plain VARCHAR columns. Values match docs/ws-protocol.md section 5."""

import enum


class UserRole(enum.StrEnum):
    ADMIN = "admin"
    USER = "user"


class DeviceStatus(enum.StrEnum):
    ACTIVE = "active"
    REVOKED = "revoked"


class RequestStatus(enum.StrEnum):
    PENDING = "pending"
    ACCEPTED = "accepted"
    REJECTED = "rejected"
    EXPIRED = "expired"
    CANCELLED = "cancelled"


class SessionStatus(enum.StrEnum):
    CONNECTING = "connecting"
    ACTIVE = "active"
    ENDED = "ended"


NON_ENDED_SESSION_STATUSES: tuple[str, ...] = (SessionStatus.CONNECTING, SessionStatus.ACTIVE)


class SessionEndReason(enum.StrEnum):
    GUEST_ENDED = "guest_ended"
    HOST_ENDED = "host_ended"
    EXPIRED = "expired"
    GUEST_DISCONNECTED = "guest_disconnected"
    HOST_DISCONNECTED = "host_disconnected"
    CONNECT_FAILED = "connect_failed"
    ADMIN_TERMINATED = "admin_terminated"
    BROWSER_NOT_PROXIED = "browser_not_proxied"
    PROTOCOL_ERROR = "protocol_error"


class SessionRole(enum.StrEnum):
    GUEST = "guest"
    HOST = "host"


class ConnectResult(enum.StrEnum):
    OK = "ok"
    FAILED = "failed"
    """The clients reported ``session.connect_failed``."""
    TIMEOUT = "timeout"
    """The connect deadline fired while the session was still ``connecting``."""


class CandidateType(enum.StrEnum):
    LAN = "lan"
    UPNP = "upnp"
    PUBLIC = "public"
    V6 = "v6"


class SecurityEventType(enum.StrEnum):
    LOGIN_SUCCESS = "login_success"
    LOGIN_FAILED = "login_failed"
    LOGIN_LOCKED = "login_locked"
    LOGOUT = "logout"
    REFRESH_REUSE = "refresh_reuse"
    DEVICE_REVOKED = "device_revoked"
    SESSION_ADMIN_TERMINATED = "session_admin_terminated"
    USER_DEACTIVATED = "user_deactivated"
    """An administrator set ``is_active: false``. Written against the user, naming the administrator
    who did it, so ``GET /admin/security-events`` can answer "who cut this person off, and when"."""
    REQUEST_AUTO_ACCEPTED = "request_auto_accepted"
    """A host answered ``request.accept`` with ``auto: true``: the request matched a trusted-guest
    rule the host had set up beforehand, so no prompt was shown. The consent is real - the host
    gave it in advance - but it is the one acceptance no human saw at the moment it happened, so
    it is on the record. Like ``listener_unauthenticated`` it never revokes anything (ADR-0008)."""
    LISTENER_UNAUTHENTICATED = "listener_unauthenticated"
    """Reported by a host client through ``POST /diagnostics``: connections that reached its
    listening port during a connect window and failed ``AUTH1``. The server cannot observe this
    itself. It describes the network *around* a device, not the device's own behaviour, so it is
    surfaced to administrators and deliberately never revokes anything (ADR-0008)."""
