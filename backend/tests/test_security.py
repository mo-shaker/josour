import uuid
from datetime import UTC, datetime

import pytest

from app.core.clock import ensure_utc
from app.core.config import Settings
from app.core.security import (
    AccessClaims,
    InvalidAccessToken,
    create_access_token,
    decode_access_token,
    generate_opaque_secret,
    hash_opaque_secret,
    hash_password,
    verify_opaque_secret,
    verify_password,
)


def test_password_hashing_roundtrip() -> None:
    digest = hash_password("hunter22")
    assert digest.startswith("$argon2id$")
    assert verify_password(digest, "hunter22")
    assert not verify_password(digest, "hunter2")
    assert not verify_password(None, "anything")
    assert not verify_password("garbage", "anything")


def test_opaque_secret_hashing() -> None:
    secret = generate_opaque_secret()
    assert len(secret) >= 43
    digest = hash_opaque_secret(secret)
    assert len(digest) == 64 and secret not in digest
    assert verify_opaque_secret(digest, secret)
    assert not verify_opaque_secret(digest, secret[:-1] + "x")


def _settings(**overrides: object) -> Settings:
    values: dict[str, object] = {
        "jwt_secret": "unit-test-secret-that-is-long-enough-32b",
        **overrides,
    }
    return Settings(_env_file=None, **values)  # type: ignore[call-arg]


def test_access_token_roundtrip_and_expiry() -> None:
    settings = _settings()
    claims = AccessClaims(user_id=uuid.uuid4(), device_id=uuid.uuid4(), role="admin")
    token, expires_in = create_access_token(settings, claims)
    assert expires_in == 900
    assert decode_access_token(settings, token) == claims

    with pytest.raises(InvalidAccessToken):
        decode_access_token(
            _settings(jwt_secret="another-secret-that-is-also-32-bytes-long"), token
        )

    expired, _ = create_access_token(_settings(access_token_minutes=-1), claims)
    with pytest.raises(InvalidAccessToken):
        decode_access_token(settings, expired)

    with pytest.raises(InvalidAccessToken):
        decode_access_token(settings, "not.a.jwt")


def test_ensure_utc() -> None:
    assert ensure_utc(None) is None
    naive = datetime(2026, 9, 3, 12, 0, 0)
    assert ensure_utc(naive) == datetime(2026, 9, 3, 12, 0, 0, tzinfo=UTC)
