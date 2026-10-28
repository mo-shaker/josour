"""String enums persisted as plain VARCHAR columns. Values match docs/ws-protocol.md section 5."""

import enum


class UserRole(enum.StrEnum):
    ADMIN = "admin"
    USER = "user"


class ApprovalStatus(enum.StrEnum):
    """Whether an administrator has let a self-registered account in (ADR-0017).

    Accounts an administrator creates are ``approved`` from the start. A ``pending`` or
    ``rejected`` account is always ``is_active = false`` too (a CHECK constraint), so every
    existing ``is_active`` gate keeps it out without having to know this column exists."""

    PENDING = "pending"
    APPROVED = "approved"
    REJECTED = "rejected"


class RegisteredVia(enum.StrEnum):
    ADMIN = "admin"
    CLI = "cli"
    SELF = "self"


class RegistrationMode(enum.StrEnum):
    """``registration_mode`` operator setting: may anyone create an account from the app?"""

    CLOSED = "closed"
    APPROVAL = "approval"
    """The account exists but cannot sign in until an administrator approves it (the default)."""
    OPEN = "open"


class RequestInitiator(enum.StrEnum):
    """Who opened a ``connection_requests`` row: a guest asking for a connection
    (``request.create``) or a host offering theirs (``offer.create``)."""

    GUEST = "guest"
    HOST = "host"


class InviteStatus(enum.StrEnum):
    PENDING = "pending"
    ACCEPTED = "accepted"
    DECLINED = "declined"
    CANCELLED = "cancelled"
    EXPIRED = "expired"


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
    CONTACT_REMOVED = "contact_removed"
    """One of the two removed the other from their contacts while the session was live."""
    SYSTEM_PROXY_FAILED = "system_proxy_failed"
    """The guest could not point its system proxy at the tunnel (ADR-0015)."""


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
    REGISTRATION_SUBMITTED = "registration_submitted"
    """Somebody created an account from the app. Written against the new account."""
    REGISTRATION_DUPLICATE = "registration_duplicate"
    """A registration named an email that already has an account. The caller got the same answer
    as a fresh registration (ADR-0017), so this row is the only place the attempt shows up."""
    USER_APPROVED = "user_approved"
    USER_REJECTED = "user_rejected"
    USER_DELETED = "user_deleted"
    USER_CREATED = "user_created"
    """An administrator (or the shell) created an account; ``details`` names the role and who did
    it. Self-registration is ``registration_submitted`` instead."""
    USER_UPDATED = "user_updated"
    """An administrator changed an account: ``details`` carries ``email_changed``, ``role``,
    ``password_set``, ``activated`` and/or ``unlocked``. Deactivation is ``user_deactivated``."""
    CONTACT_INVITE_SENT = "contact_invite_sent"
    CONTACT_ADDED = "contact_added"
    CONTACT_REMOVED = "contact_removed"
    TRUST_GRANTED = "trust_granted"
    """A user let a contact use their connection without asking each time (ADR-0014)."""
    TRUST_REVOKED = "trust_revoked"
    SETTINGS_CHANGED = "settings_changed"
    ADMIN_WEB_LOGIN_SUCCESS = "admin_web_login_success"
    ADMIN_WEB_LOGIN_FAILED = "admin_web_login_failed"
    """A dashboard sign-in that passed the password but not the rest: not an administrator, or
    a wrong second factor. Wrong passwords are ``login_failed`` like the app's."""
    ADMIN_WEB_LOGOUT = "admin_web_logout"
    TOTP_ENABLED = "totp_enabled"
    TOTP_DISABLED = "totp_disabled"
    LISTENER_UNAUTHENTICATED = "listener_unauthenticated"
    """Reported by a host client through ``POST /diagnostics``: connections that reached its
    listening port during a connect window and failed ``AUTH1``. The server cannot observe this
    itself. It describes the network *around* a device, not the device's own behaviour, so it is
    surfaced to administrators and deliberately never revokes anything (ADR-0008)."""
