"""Session tokens: what lets the relay pair two strangers without a database.

The relay holds no session state of its own. It does not know which sessions exist, who owns
them, or when they expire - the control-plane API knows all of that, so it says so in a signed
token and the relay only checks the signature.

    claims: {"sid": "<session uuid>", "role": "guest"|"host", "iat": ..., "exp": ...}

That keeps the relay stateless and cheap, and it means a relay that is compromised cannot mint
access to a session: it can only verify. The cost is that a leaked token is a bearer credential
until it expires, which is why :data:`DEFAULT_TTL` is short - a token is used within seconds of
``session.created``, so a lifetime measured in minutes is already generous.

``jwt_secret`` is deliberately NOT reused here. The API's JWT secret signs user access tokens;
giving the relay a copy would mean a relay breach could mint logins. A separate ``RELAY_SECRET``
keeps the blast radius of the internet-facing byte pump to the byte pump.
"""

from __future__ import annotations

import uuid
from datetime import UTC, datetime, timedelta

import jwt

from relay.protocol import Role

ALGORITHM = "HS256"
DEFAULT_TTL = timedelta(minutes=5)
"""How long a freshly minted token stays usable. The client dials the relay immediately after
``session.created``; this only has to cover the connect window plus clock skew."""

LEEWAY = timedelta(seconds=30)
"""Tolerated clock skew between the API host and the relay host."""


class TokenError(Exception):
    """Verification failed. The message is safe to log - it never carries the token itself."""


def mint(secret: str, session_id: uuid.UUID, role: Role, *, ttl: timedelta | None = None) -> str:
    """Called by the control-plane API when it hands a party its relay credentials."""
    if not secret:
        raise ValueError("relay secret must not be empty")
    now = datetime.now(UTC)
    payload = {
        "sid": str(session_id),
        "role": str(role),
        "iat": int(now.timestamp()),
        "exp": int((now + (ttl or DEFAULT_TTL)).timestamp()),
    }
    return jwt.encode(payload, secret, algorithm=ALGORITHM)


def verify(secret: str, token: str, session_id: uuid.UUID, role: Role) -> None:
    """Raise :class:`TokenError` unless ``token`` authorises exactly this session and this role.

    Binding the token to the role matters as much as binding it to the session: without it a
    party holding one token could open both sides and pair with itself, which would let a
    stranger who obtained a single token occupy a session slot and lock the real peer out."""
    try:
        claims = jwt.decode(
            token,
            secret,
            algorithms=[ALGORITHM],
            leeway=LEEWAY,
            options={"require": ["sid", "role", "exp"]},
        )
    except jwt.ExpiredSignatureError as exc:
        raise TokenError("token has expired") from exc
    except jwt.InvalidTokenError as exc:
        # Covers a bad signature, a missing claim, and anything that is not a JWT at all. The
        # reason is deliberately not narrowed further in the message an attacker could observe.
        raise TokenError("token is not valid") from exc

    if claims["sid"] != str(session_id):
        raise TokenError("token does not name this session")
    if claims["role"] != str(role):
        raise TokenError("token does not name this role")
