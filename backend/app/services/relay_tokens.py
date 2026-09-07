"""Minting the tokens the relay verifies (ADR-0009).

The relay holds no session state: it does not know which sessions exist or who may join one. This
module is the other half of that arrangement - the control plane, which does know, says so in a
short-lived signed token, and the relay only checks the signature.

The claim set is deliberately tiny and must stay byte-compatible with ``relay/relay/tokens.py``::

    {"sid": "<session uuid>", "role": "guest"|"host", "iat": ..., "exp": ...}

``relay_secret`` is separate from ``jwt_secret`` on purpose. The latter signs user access tokens;
handing a copy to an internet-facing byte pump would mean a relay breach could mint logins.
"""

import uuid
from datetime import UTC, datetime, timedelta

import jwt

from app.core.config import Settings
from app.models.enums import SessionRole

ALGORITHM = "HS256"

TOKEN_TTL = timedelta(minutes=5)
"""A party dials the relay within seconds of ``session.created``, so this only has to cover the
connect window plus clock skew between the API host and the relay host. A relay token is a bearer
credential until it expires, which is the whole reason it is minutes and not hours."""


def mint(settings: Settings, session_id: uuid.UUID, role: SessionRole) -> str:
    """One token for one party of one session.

    The role is bound in, not just the session: without it a party holding a single token could
    open both sides of the relay and pair with itself, taking the session and locking the real
    peer out."""
    if not settings.relay_secret:
        raise ValueError("relay_secret is not configured")
    now = datetime.now(UTC)
    payload = {
        "sid": str(session_id),
        "role": role.value,
        "iat": int(now.timestamp()),
        "exp": int((now + TOKEN_TTL).timestamp()),
    }
    return jwt.encode(payload, settings.relay_secret, algorithm=ALGORITHM)
