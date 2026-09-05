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
    password_needs_rehash,
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


# --- ADR-0007: معاملات argon2id (معتمد 2026-09-05) ---


def test_argon2_uses_the_owasp_recommended_parameters() -> None:
    """المعاملات جزء من الموقف الأمني: تغييرها يجب أن يكون قرارًا لا انزلاقًا."""
    from app.core.security import (
        ARGON2_MEMORY_COST_KIB,
        ARGON2_PARALLELISM,
        ARGON2_TIME_COST,
        _hasher,
    )

    assert (ARGON2_MEMORY_COST_KIB, ARGON2_TIME_COST, ARGON2_PARALLELISM) == (19 * 1024, 2, 1)
    assert _hasher.memory_cost == ARGON2_MEMORY_COST_KIB
    assert _hasher.time_cost == ARGON2_TIME_COST
    assert _hasher.parallelism == ARGON2_PARALLELISM
    # والمعاملات مكتوبة داخل التجزئة، وهو ما يجعل الترقية التدريجية ممكنة أصلًا
    assert "m=19456,t=2,p=1" in hash_password("Whatever-pass-1")


def test_passwords_hashed_with_the_previous_parameters_still_verify_and_are_upgraded() -> None:
    """لا يجوز أن يكسر تغيير المعاملات حساب مستخدم قائم."""
    from argon2 import PasswordHasher

    legacy = PasswordHasher(memory_cost=65536, time_cost=3, parallelism=4).hash("Existing-pass-1")

    assert verify_password(legacy, "Existing-pass-1") is True
    assert verify_password(legacy, "wrong-password") is False
    assert password_needs_rehash(legacy) is True
    assert password_needs_rehash(hash_password("Existing-pass-1")) is False
