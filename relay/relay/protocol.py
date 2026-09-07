"""The relay wire contract, server side.

The authoritative definition is ``client/src/Josour.Tunnel/Transport/RelayProtocol.cs``; this is
the other half of the same 26-byte handshake and must not drift from it:

    preamble (client -> relay), 24 fixed bytes + token:
        4B   magic = "RBRL"
        u8   version = 1
        u8   role (0 = guest, 1 = host)
        16B  session_id (UUID bytes, big-endian - the same order as AUTH1)
        u16  token_length (big-endian, 1..1024)
        NB   token (signed by the control-plane API, UTF-8)

    reply (relay -> client), 2 bytes:
        u8   version = 1
        u8   status (RelayStatus)

Everything after a ``paired`` reply is opaque: the peers run their own TLS handshake, certificate
pinning and AUTH1/AUTH2 over these bytes (docs/protocol.md sections 3 and 4). The relay never
parses, buffers by message, or inspects any of it.

The magic stays ``RBRL`` after the Josour rename: it is an opaque four-byte tag on the wire, and
both halves agree on it. Changing it would buy nothing and break every already-built client.
"""

from __future__ import annotations

import enum
import struct
import uuid
from dataclasses import dataclass

MAGIC = b"RBRL"
VERSION = 1
MAGIC_LENGTH = 4
SESSION_ID_LENGTH = 16
TOKEN_LENGTH_OFFSET = MAGIC_LENGTH + 1 + 1 + SESSION_ID_LENGTH  # 22
FIXED_PREAMBLE_LENGTH = TOKEN_LENGTH_OFFSET + 2  # 24
MAX_TOKEN_LENGTH = 1024
REPLY_LENGTH = 2

GUEST_ROLE = 0
HOST_ROLE = 1


class Role(enum.StrEnum):
    GUEST = "guest"
    HOST = "host"

    @property
    def wire(self) -> int:
        return HOST_ROLE if self is Role.HOST else GUEST_ROLE

    @classmethod
    def from_wire(cls, value: int) -> Role:
        if value == HOST_ROLE:
            return cls.HOST
        if value == GUEST_ROLE:
            return cls.GUEST
        raise ProtocolError(f"bad role {value}")


class Status(enum.IntEnum):
    """``RelayStatus`` in RelayProtocol.cs. The numbers are the wire contract."""

    PAIRED = 0
    UNAUTHORIZED = 1
    UNKNOWN_SESSION = 2
    NO_PEER = 3
    BUSY = 4
    PROTOCOL_ERROR = 5
    INTERNAL = 6

    @property
    def wire_name(self) -> str:
        return _WIRE_NAMES[self]


_WIRE_NAMES = {
    Status.PAIRED: "paired",
    Status.UNAUTHORIZED: "unauthorized",
    Status.UNKNOWN_SESSION: "unknown_session",
    Status.NO_PEER: "no_peer",
    Status.BUSY: "busy",
    Status.PROTOCOL_ERROR: "protocol_error",
    Status.INTERNAL: "internal",
}


class ProtocolError(Exception):
    """A malformed preamble. The message is safe to log: it never carries the token."""


@dataclass(frozen=True, slots=True)
class Preamble:
    session_id: uuid.UUID
    role: Role
    token: str


def build_reply(status: Status) -> bytes:
    return bytes((VERSION, int(status)))


def parse_preamble(buffer: bytes) -> Preamble:
    """Parse a complete preamble. Raises :class:`ProtocolError` with a describing, secret-free
    message on anything malformed."""
    if len(buffer) < FIXED_PREAMBLE_LENGTH:
        raise ProtocolError("preamble is shorter than the fixed header")
    if buffer[:MAGIC_LENGTH] != MAGIC:
        raise ProtocolError("bad magic")
    if buffer[MAGIC_LENGTH] != VERSION:
        raise ProtocolError(f"unsupported version {buffer[MAGIC_LENGTH]}")

    role = Role.from_wire(buffer[MAGIC_LENGTH + 1])
    session_id = uuid.UUID(bytes=buffer[MAGIC_LENGTH + 2 : MAGIC_LENGTH + 2 + SESSION_ID_LENGTH])
    (token_length,) = struct.unpack_from(">H", buffer, TOKEN_LENGTH_OFFSET)
    if token_length == 0:
        raise ProtocolError("token is empty")
    if token_length > MAX_TOKEN_LENGTH:
        raise ProtocolError("token is too long")
    if len(buffer) < FIXED_PREAMBLE_LENGTH + token_length:
        raise ProtocolError("preamble is truncated")

    raw = buffer[FIXED_PREAMBLE_LENGTH : FIXED_PREAMBLE_LENGTH + token_length]
    try:
        # strict=True mirrors the client's StrictUtf8: a malformed token is rejected, never
        # silently rewritten with U+FFFD and then handed to the verifier as different bytes.
        token = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise ProtocolError("token is not valid UTF-8") from exc

    return Preamble(session_id=session_id, role=role, token=token)


def build_preamble(session_id: uuid.UUID, role: Role, token: str) -> bytes:
    """The client's half. Only tests need it here, but keeping both directions in one module is
    what makes a drift from RelayProtocol.cs show up as a failing round-trip rather than as a
    silent incompatibility discovered on a real session."""
    token_bytes = token.encode("utf-8")
    if not token_bytes:
        raise ValueError("token must not be empty")
    if len(token_bytes) > MAX_TOKEN_LENGTH:
        raise ValueError(f"token must not exceed {MAX_TOKEN_LENGTH} bytes when UTF-8 encoded")
    return b"".join(
        (
            MAGIC,
            bytes((VERSION, role.wire)),
            session_id.bytes,
            struct.pack(">H", len(token_bytes)),
            token_bytes,
        )
    )
