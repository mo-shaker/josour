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
    # TODO(week 4/5): listener_unauthenticated (from client diagnostics)
