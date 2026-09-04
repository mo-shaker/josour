"""Wire contract of ``/ws`` (docs/ws-protocol.md sections 2-4, 7).

Every frame is one JSON object with a mandatory ``type``. Client frames are parsed through a
discriminated union; unknown or malformed frames never raise out of the handler, they become an
``error`` frame with code ``bad_request`` that echoes the client's ``ref`` when there is one.

Field names here are the wire names, literally as documented; the C# mirror of these models is
``client/src/RouteBridge.Core/Control/ControlMessages.cs``.
"""

import enum
import json
import uuid
from datetime import datetime
from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, PlainSerializer, TypeAdapter, ValidationError

from app.core.clock import ensure_utc

# ---------------------------------------------------------------- close codes / error codes


class CloseCode(enum.IntEnum):
    """docs/ws-protocol.md section 7."""

    NO_HELLO = 4401
    """No valid ``hello`` arrived within the deadline (or its token was rejected)."""
    NOT_ALLOWED = 4403
    """Device revoked or user disabled."""
    SUPERSEDED = 4409
    """A newer connection for the same ``device_id`` took over."""
    SERVER_RESTART = 1012
    """Server shutting down; clients reconnect with exponential backoff."""
    NORMAL = 1000
    GOING_AWAY = 1001
    """Heartbeat timeout: the peer stopped answering ``ping``. The contract names no dedicated
    code for it, so the standard "going away" is used."""
    INTERNAL_ERROR = 1011
    """The handshake itself failed unexpectedly; also not named by the contract."""


class ErrorCode(enum.StrEnum):
    """The ``error.code`` vocabulary (docs/ws-protocol.md section 2)."""

    UNAUTHORIZED = "unauthorized"
    BAD_REQUEST = "bad_request"
    NOT_FOUND = "not_found"
    HOST_UNAVAILABLE = "host_unavailable"
    SESSION_EXISTS = "session_exists"
    REQUEST_PENDING = "request_pending"
    FORBIDDEN = "forbidden"
    RATE_LIMITED = "rate_limited"
    INTERNAL = "internal"


class WsError(Exception):
    """Raised by the WebSocket services; the connection turns it into one ``error`` frame."""

    def __init__(self, code: ErrorCode, message: str, *, ref: str | None = None) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.ref = ref

    def to_message(self, ref: str | None = None) -> "ErrorFrame":
        return ErrorFrame(ref=self.ref or ref, code=self.code, message=self.message)


# ---------------------------------------------------------------- shared field types


def _iso_z(value: datetime) -> str:
    """ISO-8601 UTC with the ``Z`` suffix (section 2). SQLite hands back naive datetimes."""
    aware = ensure_utc(value)
    assert aware is not None
    return aware.isoformat().replace("+00:00", "Z")


UtcTime = Annotated[datetime, PlainSerializer(_iso_z, return_type=str, when_used="json")]


# ---------------------------------------------------------------- client -> server (section 3)


class _ClientFrame(BaseModel):
    # Unknown extra keys are tolerated so a newer client cannot be broken by an older server.
    model_config = ConfigDict(extra="ignore")


class Hello(_ClientFrame):
    type: Literal["hello"]
    token: str
    device_id: uuid.UUID
    app_version: str | None = None
    diagnostics: dict[str, Any] | None = None


class HostAvailable(_ClientFrame):
    type: Literal["host.available"]
    available: bool
    listen_port: int | None = Field(default=None, ge=1, le=65535)


class RequestCreate(_ClientFrame):
    type: Literal["request.create"]
    ref: str
    host_device_id: uuid.UUID
    duration_min: int


class RequestCancel(_ClientFrame):
    type: Literal["request.cancel"]
    ref: str
    request_id: uuid.UUID


class RequestAccept(_ClientFrame):
    type: Literal["request.accept"]
    ref: str
    request_id: uuid.UUID


class RequestReject(_ClientFrame):
    type: Literal["request.reject"]
    ref: str
    request_id: uuid.UUID


class ClientPing(_ClientFrame):
    type: Literal["ping"]


class ClientPong(_ClientFrame):
    type: Literal["pong"]


# TODO(week 4): session.endpoint, session.connected, session.connect_failed, session.stats and
# session.end join this union. Until then they are unknown types and answered with bad_request.
type ClientMessage = Annotated[
    Hello
    | HostAvailable
    | RequestCreate
    | RequestCancel
    | RequestAccept
    | RequestReject
    | ClientPing
    | ClientPong,
    Field(discriminator="type"),
]

_CLIENT_ADAPTER: TypeAdapter[ClientMessage] = TypeAdapter(ClientMessage)


# ---------------------------------------------------------------- server -> client (section 4)


class ServerMessage(BaseModel):
    """Base for every server frame. ``to_json`` emits exactly the documented field names."""

    def to_json(self) -> str:
        return self.model_dump_json()


class ServerSettings(BaseModel):
    max_session_minutes: int
    request_timeout_seconds: int
    allowed_ports: list[int]
    log_domains: bool


class HelloAck(ServerMessage):
    type: Literal["hello.ack"] = "hello.ack"
    server_time: UtcTime
    public_ip: str
    settings: ServerSettings
    allowlist_version: int


class HostEntry(BaseModel):
    device_id: uuid.UUID
    user_display_name: str
    device_name: str
    reachable: bool | None


class HostsSnapshot(ServerMessage):
    type: Literal["hosts.snapshot"] = "hosts.snapshot"
    hosts: list[HostEntry]


class HostsUpdate(ServerMessage):
    type: Literal["hosts.update"] = "hosts.update"
    hosts: list[HostEntry]


class RequestCreated(ServerMessage):
    type: Literal["request.created"] = "request.created"
    ref: str
    request_id: uuid.UUID
    expires_at: UtcTime


class RequestIncoming(ServerMessage):
    type: Literal["request.incoming"] = "request.incoming"
    request_id: uuid.UUID
    guest_name: str
    guest_device: str
    duration_min: int
    allowlist_version: int
    expires_at: UtcTime


class RequestResult(ServerMessage):
    type: Literal["request.result"] = "request.result"
    request_id: uuid.UUID
    accepted: bool
    reason: str | None = None
    session_id: uuid.UUID | None = None


class RequestExpired(ServerMessage):
    type: Literal["request.expired"] = "request.expired"
    request_id: uuid.UUID


class Peer(BaseModel):
    user_display_name: str
    device_name: str


class SessionCreated(ServerMessage):
    type: Literal["session.created"] = "session.created"
    session_id: uuid.UUID
    role: Literal["guest", "host"]
    secret_b64: str
    expires_at: UtcTime
    allowlist_version: int
    peer_public_ip: str
    same_public_ip: bool
    peer: Peer


class SessionTerminate(ServerMessage):
    type: Literal["session.terminate"] = "session.terminate"
    session_id: uuid.UUID
    reason: str


class AllowlistUpdated(ServerMessage):
    type: Literal["allowlist.updated"] = "allowlist.updated"
    version: int


class ErrorFrame(ServerMessage):
    type: Literal["error"] = "error"
    ref: str | None = None
    code: ErrorCode
    message: str


class Ping(ServerMessage):
    type: Literal["ping"] = "ping"


class Pong(ServerMessage):
    type: Literal["pong"] = "pong"


# TODO(week 4): session.peer_endpoint and session.active frames.


# ---------------------------------------------------------------- parsing


def message_ref(message: object) -> str | None:
    """The ``ref`` of a client frame, if its type carries one."""
    return getattr(message, "ref", None)


def _raw_ref(payload: object) -> str | None:
    if isinstance(payload, dict):
        ref = payload.get("ref")
        if isinstance(ref, str) and ref:
            return ref
    return None


def _format_errors(exc: ValidationError) -> str:
    """Never includes the offending input: a malformed ``hello`` carries an access token."""
    parts = []
    for err in exc.errors():
        loc = ".".join(str(p) for p in err.get("loc", ()) if p != "function-after")
        msg = str(err.get("msg", "invalid"))
        parts.append(f"{loc}: {msg}" if loc else msg)
    return "; ".join(parts[:5]) or "invalid message"


def parse_client_message(raw: str) -> ClientMessage:
    """Parse one client frame. Raises :class:`WsError` (``bad_request``) with the ``ref`` of the
    frame when the payload was JSON that carried one."""
    try:
        payload = json.loads(raw)
    except (ValueError, TypeError) as exc:
        raise WsError(ErrorCode.BAD_REQUEST, "Frame is not valid JSON") from exc
    if not isinstance(payload, dict):
        raise WsError(ErrorCode.BAD_REQUEST, "Frame must be a JSON object")
    ref = _raw_ref(payload)
    try:
        return _CLIENT_ADAPTER.validate_python(payload)
    except ValidationError as exc:
        raise WsError(ErrorCode.BAD_REQUEST, _format_errors(exc), ref=ref) from exc
