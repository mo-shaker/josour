"""The wire contract. These constants and offsets are shared with RelayProtocol.cs; if one side
moves, a real session fails with a preamble error nobody can read, so they are pinned here."""

from __future__ import annotations

import struct
import uuid

import pytest

from relay.protocol import (
    FIXED_PREAMBLE_LENGTH,
    MAGIC,
    MAX_TOKEN_LENGTH,
    REPLY_LENGTH,
    TOKEN_LENGTH_OFFSET,
    VERSION,
    ProtocolError,
    Role,
    Status,
    build_preamble,
    build_reply,
    parse_preamble,
)

SESSION = uuid.UUID("d1a8ac80-19b4-473d-a6a0-d45fe4c3beb3")


def test_layout_matches_the_csharp_constants() -> None:
    assert MAGIC == b"RBRL"
    assert VERSION == 1
    assert TOKEN_LENGTH_OFFSET == 22
    assert FIXED_PREAMBLE_LENGTH == 24
    assert MAX_TOKEN_LENGTH == 1024
    assert REPLY_LENGTH == 2


@pytest.mark.parametrize("role", [Role.GUEST, Role.HOST])
def test_preamble_round_trip(role: Role) -> None:
    parsed = parse_preamble(build_preamble(SESSION, role, "tok"))
    assert parsed.session_id == SESSION
    assert parsed.role is role
    assert parsed.token == "tok"


def test_role_bytes_are_the_wire_contract() -> None:
    assert Role.GUEST.wire == 0
    assert Role.HOST.wire == 1
    assert build_preamble(SESSION, Role.HOST, "t")[5] == 1
    assert build_preamble(SESSION, Role.GUEST, "t")[5] == 0


def test_session_id_is_big_endian_uuid_bytes() -> None:
    """The same order AUTH1 uses (RFC 4122), not .NET's mixed-endian Guid layout."""
    assert build_preamble(SESSION, Role.GUEST, "t")[6:22] == SESSION.bytes


def test_status_numbers_are_the_wire_contract() -> None:
    assert [int(s) for s in Status] == [0, 1, 2, 3, 4, 5, 6]
    assert Status.PAIRED.wire_name == "paired"
    assert Status.NO_PEER.wire_name == "no_peer"


def test_reply_carries_version_and_status() -> None:
    assert build_reply(Status.PAIRED) == bytes((1, 0))
    assert build_reply(Status.BUSY) == bytes((1, 4))


def test_token_at_the_maximum_length_is_accepted() -> None:
    token = "x" * MAX_TOKEN_LENGTH
    assert parse_preamble(build_preamble(SESSION, Role.HOST, token)).token == token


def test_multibyte_token_is_measured_in_bytes_not_characters() -> None:
    token = "ج" * 8  # 2 bytes each
    raw = build_preamble(SESSION, Role.GUEST, token)
    assert struct.unpack_from(">H", raw, TOKEN_LENGTH_OFFSET)[0] == 16
    assert parse_preamble(raw).token == token


def test_building_rejects_an_oversized_token() -> None:
    with pytest.raises(ValueError):
        build_preamble(SESSION, Role.GUEST, "x" * (MAX_TOKEN_LENGTH + 1))


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda b: b[:10], "shorter than the fixed header"),
        (lambda b: b"XXXX" + b[4:], "bad magic"),
        (lambda b: b[:4] + bytes((9,)) + b[5:], "unsupported version"),
        (lambda b: b[:5] + bytes((7,)) + b[6:], "bad role"),
        (lambda b: b[:22] + struct.pack(">H", 0) + b[24:], "token is empty"),
        (lambda b: b[:22] + struct.pack(">H", 4096) + b[24:], "token is too long"),
        (lambda b: b[:22] + struct.pack(">H", 99) + b[24:], "truncated"),
    ],
)
def test_malformed_preambles_are_rejected(mutate, message: str) -> None:
    raw = build_preamble(SESSION, Role.GUEST, "token")
    with pytest.raises(ProtocolError, match=message):
        parse_preamble(mutate(raw))


def test_invalid_utf8_token_is_rejected_not_silently_rewritten() -> None:
    """The C# side made this explicit after finding the rejection branch was dead: a malformed
    token must fail, never be re-encoded with U+FFFD and passed on as different bytes."""
    raw = build_preamble(SESSION, Role.GUEST, "aaaa")
    broken = raw[:FIXED_PREAMBLE_LENGTH] + b"\xff\xfe\xfd\xfc"
    with pytest.raises(ProtocolError, match="not valid UTF-8"):
        parse_preamble(broken)


def test_trailing_bytes_after_the_token_are_ignored() -> None:
    """The relay reads exactly token_length bytes; whatever follows is tunnel payload."""
    raw = build_preamble(SESSION, Role.HOST, "tok") + b"ciphertext"
    assert parse_preamble(raw).token == "tok"
