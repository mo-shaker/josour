"""RFC 6238 time-based one-time passwords for the web dashboard (ADR-0016).

SHA-1, 30-second steps, 6 digits - what every authenticator app does by default. A code is
accepted for the current step and one step either side (clock drift), and never twice: the
caller stores the step it accepted and passes it back as ``last_step``.
"""

import base64
import hashlib
import hmac
import secrets
import struct
import time
from urllib.parse import quote

STEP_SECONDS = 30
DIGITS = 6
WINDOW = 1


def generate_secret() -> str:
    """160 random bits, base32 without padding (what authenticator apps expect)."""
    return base64.b32encode(secrets.token_bytes(20)).decode("ascii").rstrip("=")


def _key(secret: str) -> bytes:
    padded = secret.upper() + "=" * (-len(secret) % 8)
    return base64.b32decode(padded)


def code_at(secret: str, step: int) -> str:
    digest = hmac.new(_key(secret), struct.pack(">Q", step), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset : offset + 4])[0] & 0x7FFFFFFF
    return str(value % 10**DIGITS).zfill(DIGITS)


def current_step(now: float | None = None) -> int:
    return int((time.time() if now is None else now) // STEP_SECONDS)


def verify(
    secret: str, code: str, *, last_step: int | None, now: float | None = None
) -> int | None:
    """The step ``code`` matched, or ``None``. A step at or before ``last_step`` never matches."""
    code = "".join(code.split())
    if len(code) != DIGITS or not code.isdigit():
        return None
    step = current_step(now)
    for candidate in range(step - WINDOW, step + WINDOW + 1):
        if last_step is not None and candidate <= last_step:
            continue
        if hmac.compare_digest(code_at(secret, candidate), code):
            return candidate
    return None


def provisioning_uri(secret: str, *, account: str, issuer: str = "Josour") -> str:
    """The ``otpauth://`` address an authenticator app imports (typed or pasted here: no QR)."""
    label = quote(f"{issuer}:{account}")
    return f"otpauth://totp/{label}?secret={secret}&issuer={quote(issuer)}&digits={DIGITS}&period={STEP_SECONDS}"
