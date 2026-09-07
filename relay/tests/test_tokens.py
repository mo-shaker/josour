"""Token verification. A token is the only thing standing between a stranger and a session slot,
so every way of getting one wrong is pinned here."""

from __future__ import annotations

import uuid
from datetime import timedelta

import jwt
import pytest

from relay.protocol import Role
from relay.tokens import ALGORITHM, TokenError, mint, verify

SECRET = "relay-secret-that-is-at-least-32-bytes-long"
OTHER_SECRET = "a-different-secret-also-at-least-32-bytes"
SESSION = uuid.UUID("06aff63f-7a3e-4d18-a2c5-82e04f5b4f31")


def test_minted_token_verifies() -> None:
    verify(SECRET, mint(SECRET, SESSION, Role.HOST), SESSION, Role.HOST)


def test_token_is_rejected_under_a_different_secret() -> None:
    with pytest.raises(TokenError, match="not valid"):
        verify(OTHER_SECRET, mint(SECRET, SESSION, Role.HOST), SESSION, Role.HOST)


def test_token_is_bound_to_its_session() -> None:
    token = mint(SECRET, SESSION, Role.GUEST)
    with pytest.raises(TokenError, match="does not name this session"):
        verify(SECRET, token, uuid.uuid4(), Role.GUEST)


def test_token_is_bound_to_its_role() -> None:
    """Without this a party holding one token could open both sides and pair with itself,
    occupying the session and locking the real peer out."""
    token = mint(SECRET, SESSION, Role.GUEST)
    with pytest.raises(TokenError, match="does not name this role"):
        verify(SECRET, token, SESSION, Role.HOST)


def test_expired_token_is_rejected() -> None:
    token = mint(SECRET, SESSION, Role.HOST, ttl=timedelta(seconds=-120))
    with pytest.raises(TokenError, match="expired"):
        verify(SECRET, token, SESSION, Role.HOST)


def test_small_clock_skew_is_tolerated() -> None:
    """The API and the relay are different hosts; a few seconds of drift must not fail a session."""
    verify(SECRET, mint(SECRET, SESSION, Role.HOST, ttl=timedelta(seconds=-5)), SESSION, Role.HOST)


@pytest.mark.parametrize("token", ["", "not-a-jwt", "a.b.c", "x" * 200])
def test_garbage_is_rejected(token: str) -> None:
    with pytest.raises(TokenError):
        verify(SECRET, token, SESSION, Role.HOST)


def test_unsigned_token_is_rejected() -> None:
    """The classic JWT trap: an attacker re-signs with alg=none and drops the signature."""
    forged = jwt.encode(
        {"sid": str(SESSION), "role": "host", "exp": 9999999999}, "", algorithm="none"
    )
    with pytest.raises(TokenError):
        verify(SECRET, forged, SESSION, Role.HOST)


def test_token_missing_a_required_claim_is_rejected() -> None:
    forged = jwt.encode({"sid": str(SESSION), "exp": 9999999999}, SECRET, algorithm=ALGORITHM)
    with pytest.raises(TokenError):
        verify(SECRET, forged, SESSION, Role.HOST)


def test_minting_requires_a_secret() -> None:
    with pytest.raises(ValueError):
        mint("", SESSION, Role.HOST)
