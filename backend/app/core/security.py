"""Password hashing (argon2id), opaque secrets (device secret / refresh token) and access JWTs.

Nothing in this module logs; callers must never log the plaintext values handled here.

argon2id is deliberately expensive: even at the ADR-0007 parameters one hash costs
~35 ms and 64 MiB here, and roughly four times that on the single-core VPS the product targets,
because the four lanes cannot run in parallel there. The server is one uvicorn worker
(docs/Josour-MVP-Implementation-Plan.md section 2), so a hash computed on the event loop
stops *every* live control channel for that long - heartbeats, ``hosts.update``, session frames
and all. :func:`hash_password_async` and :func:`verify_password_async` therefore run the KDF on
a worker thread (argon2 releases the GIL), which is what every ``await``-ing caller must use;
the synchronous forms remain for start-up and tests.
"""

import asyncio
import hashlib
import hmac
import secrets
import uuid
from dataclasses import dataclass
from datetime import timedelta

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError, VerifyMismatchError

from app.core.clock import utcnow
from app.core.config import Settings

# argon2id with the OWASP-recommended parameters (m=19 MiB, t=2, p=1) — ADR-0007, approved
# 2026-09-05. The library's own defaults (m=64 MiB, t=3, p=4) cost ~210 ms of CPU per login, and
# the deployment target is a single Uvicorn worker on 1 vCPU (plan section 2 and 10), where a
# login burst measurably starves the event loop (docs/load-test-week5.md). These parameters are a
# published recommendation for password storage behind rate limiting and account lockout, both of
# which this server implements. Parameters live inside the hash, so existing passwords keep
# verifying and are re-hashed on the owner's next successful login (see auth.py).
ARGON2_MEMORY_COST_KIB = 19 * 1024
ARGON2_TIME_COST = 2
ARGON2_PARALLELISM = 1

_hasher = PasswordHasher(
    memory_cost=ARGON2_MEMORY_COST_KIB,
    time_cost=ARGON2_TIME_COST,
    parallelism=ARGON2_PARALLELISM,
)

# Verified against when the user does not exist so that response timing does not reveal it.
_DUMMY_HASH = _hasher.hash(secrets.token_urlsafe(16))


def hash_password(password: str) -> str:
    return _hasher.hash(password)


def verify_password(password_hash: str | None, password: str) -> bool:
    """Constant-time-ish verification; always performs an argon2 verify even for unknown users."""
    try:
        return _hasher.verify(password_hash or _DUMMY_HASH, password)
    except (VerifyMismatchError, VerificationError, InvalidHashError):
        return False


MAX_CONCURRENT_KDF = 2
"""How many argon2 operations may run at once.

Each one holds ``memory_cost`` (19 MiB per ADR-0007) for its whole duration, and the target host
is 1 vCPU / 2 GB (plan section 10), where extra parallel hashes buy no throughput — the single
core is the bottleneck — and only multiply the peak. Two slots cap the KDF at ~38 MiB; callers
queue on the semaphore without blocking the event loop, so a login burst becomes back-pressure on
logins rather than memory pressure on the box."""

_kdf_slots = asyncio.Semaphore(MAX_CONCURRENT_KDF)


async def hash_password_async(password: str) -> str:
    """:func:`hash_password` off the event loop; use this from any async caller."""
    async with _kdf_slots:
        return await asyncio.to_thread(hash_password, password)


async def verify_password_async(password_hash: str | None, password: str) -> bool:
    """:func:`verify_password` off the event loop; use this from any async caller."""
    async with _kdf_slots:
        return await asyncio.to_thread(verify_password, password_hash, password)


def password_needs_rehash(password_hash: str) -> bool:
    return _hasher.check_needs_rehash(password_hash)


def generate_opaque_secret() -> str:
    """32 random bytes, URL-safe base64 without padding (43 chars). Used for device secrets
    and refresh tokens. Only the SHA-256 of the value is ever stored."""
    return secrets.token_urlsafe(32)


def hash_opaque_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode("utf-8")).hexdigest()


def verify_opaque_secret(stored_hash: str, presented: str) -> bool:
    return hmac.compare_digest(stored_hash, hash_opaque_secret(presented))


@dataclass(frozen=True, slots=True)
class AccessClaims:
    user_id: uuid.UUID
    device_id: uuid.UUID
    role: str


class InvalidAccessToken(Exception):
    """Raised when an access JWT is missing, malformed, expired or has bad claims."""


def create_access_token(settings: Settings, claims: AccessClaims) -> tuple[str, int]:
    """Return (token, expires_in_seconds)."""
    if not settings.jwt_secret:
        raise RuntimeError("JWT_SECRET is not configured")
    now = utcnow()
    ttl = timedelta(minutes=settings.access_token_minutes)
    payload = {
        "sub": str(claims.user_id),
        "dev": str(claims.device_id),
        "role": claims.role,
        "typ": "access",
        "iat": int(now.timestamp()),
        "exp": int((now + ttl).timestamp()),
    }
    token = jwt.encode(payload, settings.jwt_secret, algorithm="HS256")
    return token, int(ttl.total_seconds())


def decode_access_token(settings: Settings, token: str) -> AccessClaims:
    try:
        payload = jwt.decode(
            token,
            settings.jwt_secret,
            algorithms=["HS256"],
            options={"require": ["sub", "dev", "role", "exp"]},
        )
    except jwt.PyJWTError as exc:
        raise InvalidAccessToken(str(exc)) from exc
    if payload.get("typ") != "access":
        raise InvalidAccessToken("not an access token")
    try:
        return AccessClaims(
            user_id=uuid.UUID(str(payload["sub"])),
            device_id=uuid.UUID(str(payload["dev"])),
            role=str(payload["role"]),
        )
    except ValueError as exc:
        raise InvalidAccessToken("malformed claims") from exc
