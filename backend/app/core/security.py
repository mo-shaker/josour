"""Password hashing (argon2id), opaque secrets (device secret / refresh token) and access JWTs.

Nothing in this module logs; callers must never log the plaintext values handled here.
"""

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

_hasher = PasswordHasher()  # argon2id with the library's current recommended parameters

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
