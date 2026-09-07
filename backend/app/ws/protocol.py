"""Wire contract of ``/ws`` (docs/ws-protocol.md sections 2-4, 7).

Every frame is one JSON object with a mandatory ``type``. Client frames are parsed through a
discriminated union; unknown or malformed frames never raise out of the handler, they become an
``error`` frame with code ``bad_request`` that echoes the client's ``ref`` when there is one.

Field names here are the wire names, literally as documented; the C# mirror of these models is
``client/src/Josour.Core/Control/ControlMessages.cs``.
"""

import enum
import ipaddress
import json
import re
import uuid
from datetime import datetime
from typing import Annotated, Any, Literal

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    PlainSerializer,
    TypeAdapter,
    ValidationError,
    field_validator,
)

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

MAX_CANDIDATES = 16
"""Both parties gather LAN + v6 + UPnP + public addresses; 16 is far above any real gather and
bounds what ``session_keys`` stores and what the server forwards."""

MAX_CONNECT_MS = 2_147_483_647
"""``sessions.connect_ms`` is a 32-bit integer column."""

MAX_BYTES = 9_223_372_036_854_775_807
"""``sessions.bytes_up`` / ``bytes_down`` are 64-bit integer columns."""

_CERT_FP = re.compile(r"[0-9a-f]{64}")


def _cert_fingerprint(value: str) -> str:
    """SHA-256 of the peer's self-signed certificate: 64 lowercase hex characters (section 3)."""
    if _CERT_FP.fullmatch(value) is None:
        raise ValueError("must be 64 lowercase hexadecimal characters")
    return value


CertFingerprint = Annotated[str, AfterValidator(_cert_fingerprint)]

type CandidateType = Literal["lan", "upnp", "public", "v6"]
"""Section 3; the C# mirror is ``Josour.Core.Tunnel.CandidateTypeNames``."""


class Candidate(BaseModel):
    """One address the peer may dial. Also the element type of ``session.peer_endpoint``, which
    is the same list forwarded verbatim to the other party."""

    model_config = ConfigDict(extra="ignore")

    type: CandidateType
    ip: str
    port: int = Field(ge=1, le=65535)

    @field_validator("ip")
    @classmethod
    def _ip(cls, value: str) -> str:
        """A literal address, canonicalised. The server never dials it, but this is data it
        stores and forwards, so it must not become a channel for arbitrary strings."""
        try:
            return str(ipaddress.ip_address(value.strip()))
        except ValueError as exc:
            raise ValueError("must be an IPv4 or IPv6 address") from exc

    @property
    def identity(self) -> tuple[str, str, int]:
        return (self.type, self.ip, self.port)


def _unique_candidates(value: list[Candidate]) -> list[Candidate]:
    if len(value) > MAX_CANDIDATES:
        raise ValueError(f"at most {MAX_CANDIDATES} candidates are accepted")
    seen: set[tuple[str, str, int]] = set()
    for candidate in value:
        if candidate.identity in seen:
            raise ValueError("candidates must not repeat the same type/ip/port")
        seen.add(candidate.identity)
    return value


CandidateList = Annotated[list[Candidate], AfterValidator(_unique_candidates)]


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


class SessionEndpoint(_ClientFrame):
    """Sent by both parties after ``session.created``; the server stores it and forwards it."""

    type: Literal["session.endpoint"]
    session_id: uuid.UUID
    cert_fp_sha256: CertFingerprint
    candidates: CandidateList


class SessionConnected(_ClientFrame):
    """**Host only** (section 5): the tunnel is up, the session becomes ``active``."""

    type: Literal["session.connected"]
    session_id: uuid.UUID
    winner_type: CandidateType
    connect_ms: int = Field(ge=0, le=MAX_CONNECT_MS)
    tls_version: Literal["1.2", "1.3"]


class SessionConnectFailed(_ClientFrame):
    type: Literal["session.connect_failed"]
    session_id: uuid.UUID
    diagnostics: dict[str, Any]
    """Free-form: the candidates tried, and the timing and error of each."""


class SessionStats(_ClientFrame):
    """Host only, every 30 s while the session is active (section 3)."""

    type: Literal["session.stats"]
    session_id: uuid.UUID
    bytes_up: int = Field(ge=0, le=MAX_BYTES)
    bytes_down: int = Field(ge=0, le=MAX_BYTES)


type ClientEndReason = Literal[
    "guest_ended",
    "host_ended",
    "browser_not_proxied",
    "protocol_error",
    "guest_disconnected",
    "host_disconnected",
]
"""The subset of the section 5 end reasons a client may ask for.

``guest_ended`` / ``host_ended`` name the sender; ``guest_disconnected`` / ``host_disconnected``
name the *peer* and are how a client reports a dead tunnel while its own control channel is still
up — the only moment the server cannot observe that itself. ``expired``, ``connect_failed`` and
``admin_terminated`` stay server verdicts and are rejected here."""


class SessionEnd(_ClientFrame):
    type: Literal["session.end"]
    session_id: uuid.UUID
    reason: ClientEndReason
    bytes_up: int = Field(ge=0, le=MAX_BYTES)
    bytes_down: int = Field(ge=0, le=MAX_BYTES)
    domains: list[str] = Field(default_factory=list)
    """Only ever persisted when the ``log_domains`` setting is on; sanitised by the service."""


class ClientPing(_ClientFrame):
    type: Literal["ping"]


class ClientPong(_ClientFrame):
    type: Literal["pong"]


type ClientMessage = Annotated[
    Hello
    | HostAvailable
    | RequestCreate
    | RequestCancel
    | RequestAccept
    | RequestReject
    | SessionEndpoint
    | SessionConnected
    | SessionConnectFailed
    | SessionStats
    | SessionEnd
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


class SessionPeerEndpoint(ServerMessage):
    """The other party's ``session.endpoint``, forwarded unchanged (section 4)."""

    type: Literal["session.peer_endpoint"] = "session.peer_endpoint"
    session_id: uuid.UUID
    cert_fp_sha256: str
    candidates: list[Candidate]


class SessionActive(ServerMessage):
    type: Literal["session.active"] = "session.active"
    session_id: uuid.UUID
    expires_at: UtcTime


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
