"""The server never logs browsing content, credentials or key material.

Product document sections 14 ("Not logging browsing content") and 15 ("Recording
administrative session data only"), and the deployed check ``scripts/security/check-logs-clean.sh``
- whose patterns are
mirrored in :data:`FORBIDDEN_PATTERNS` so the same rule is enforced before a release, not only
after one.

The test drives a whole session lifecycle with ``log_domains`` deliberately **on** (the setting
that persists host names at all) and with real-looking browsing data flowing through
``session.end``, then renders every emitted record exactly as production does - through
``JsonFormatter``, which serialises the ``extra`` fields too - and greps the result.
"""

import logging
import re
import uuid
from typing import Any

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.logging import (
    STATEMENT_LOGGERS,
    JsonFormatter,
    RedactingFilter,
    configure_logging,
)
from app.models import Session, SessionDomain, SessionKey
from app.schemas.settings import SettingsPatch
from app.services.app_settings import settings_service
from tests.conftest import ActorFactory, WsFactory

GUEST_FP = "a" * 64
HOST_FP = "b" * 64

BROWSED_DOMAINS = ["mail.example.com", "intranet.partner.example", "Docs.Example.COM."]
"""What the client reports in ``session.end``: host names only, never paths or content."""

FORBIDDEN_PATTERNS: tuple[tuple[str, str], ...] = (
    ("bearer header", r"Authorization: *Bearer"),
    ("password", r"password\"? *[:=]"),
    ("refresh token", r"refresh_token\"? *[:=] *\"?[A-Za-z0-9_-]{20}"),
    ("session secret", r"secret_b64\"? *[:=] *\"?[A-Za-z0-9+/]{20}"),
    ("cookie", r"Set-Cookie"),
    ("proxy CONNECT line", r"CONNECT [a-z0-9.-]+:443"),
    ("request line with URL", r"(GET|POST|PUT|HEAD) https?://"),
    ("bare URL", r"https?://[a-z0-9.-]+/"),
    ("device secret", r"device_secret\"? *[:=] *\"?[A-Za-z0-9_-]{20}"),
)
"""The patterns of scripts/security/check-logs-clean.sh, plus two the script cannot see from
outside: a bare URL anywhere, and a device secret."""


class _Recorder(logging.Handler):
    """Captures records the way the deployed handler renders them (formatter + redaction)."""

    def __init__(self) -> None:
        super().__init__(level=logging.DEBUG)
        self.setFormatter(JsonFormatter())
        self.addFilter(RedactingFilter())
        self.lines: list[str] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(self.format(record))


@pytest.fixture
def logs() -> Any:
    """Capture at the most verbose setting the application ever configures (``ENV=dev`` runs
    the root logger at DEBUG), through the real :func:`configure_logging`."""
    root = logging.getLogger()
    previous_handlers = list(root.handlers)
    previous_level, previous_disable = root.level, logging.root.manager.disable
    logging.disable(logging.NOTSET)
    configure_logging("DEBUG")
    handler = _Recorder()
    root.addHandler(handler)
    try:
        yield handler
    finally:
        root.removeHandler(handler)
        root.handlers[:] = previous_handlers
        root.setLevel(previous_level)
        logging.disable(previous_disable)
        for name in STATEMENT_LOGGERS:
            logging.getLogger(name).setLevel(logging.NOTSET)


async def _drive_full_session(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> tuple[str, str, str]:
    """request -> accept -> endpoints -> connected -> active -> stats -> end. Returns
    ``(session_id, secret_b64, guest_token)``."""
    guest = await make_actor("guest@example.com", display_name="Guest", device_name="GUEST-PC")
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host, client=("198.51.100.9", 51000))

    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.expect("hosts.update", skip=frozenset())
    await guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": host.device_id,
            "duration_min": 30,
        }
    )
    await guest_ws.expect("request.created")
    incoming = await host_ws.expect("request.incoming")
    await host_ws.send(
        {"type": "request.accept", "ref": "h1", "request_id": incoming["request_id"]}
    )
    session_id = (await guest_ws.expect("request.result"))["session_id"]
    created = await guest_ws.expect("session.created")
    await host_ws.expect("session.created")

    for ws, fp, ip in ((guest_ws, GUEST_FP, "192.168.1.5"), (host_ws, HOST_FP, "192.168.1.9")):
        await ws.send(
            {
                "type": "session.endpoint",
                "session_id": session_id,
                "cert_fp_sha256": fp,
                "candidates": [{"type": "lan", "ip": ip, "port": 41000}],
            }
        )
    # Section 9: the second sender receives its peer's endpoint twice (forward, then the
    # order-independent reply), so drain rather than counting frames.
    for ws in (guest_ws, host_ws):
        received = [f for f in await ws.drain(timeout=0.2) if f["type"] == "session.peer_endpoint"]
        assert received, "each party must learn its peer's endpoint"

    await host_ws.send(
        {
            "type": "session.connected",
            "session_id": session_id,
            "winner_type": "lan",
            "connect_ms": 187,
            "tls_version": "1.3",
        }
    )
    await guest_ws.expect("session.active")
    await host_ws.expect("session.active")
    await host_ws.send(
        {
            "type": "session.stats",
            "session_id": session_id,
            "bytes_up": 4096,
            "bytes_down": 65536,
        }
    )
    await guest_ws.send(
        {
            "type": "session.end",
            "session_id": session_id,
            "reason": "guest_ended",
            "bytes_up": 8192,
            "bytes_down": 131072,
            "domains": BROWSED_DOMAINS,
        }
    )
    await guest_ws.expect("session.terminate")
    await host_ws.expect("session.terminate")
    return session_id, created["secret_b64"], guest.token


async def test_a_full_session_logs_no_browsing_content_or_secrets(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, logs: Any
) -> None:
    await settings_service.update(db, SettingsPatch(log_domains=True))
    await db.commit()

    session_id, secret_b64, guest_token = await _drive_full_session(ws_connect, make_actor)
    assert logs.lines, "the lifecycle must actually have logged something to be worth scanning"
    blob = "\n".join(logs.lines)

    for name, pattern in FORBIDDEN_PATTERNS:
        assert re.search(pattern, blob, re.IGNORECASE) is None, f"logs leak {name}:\n{blob}"

    # The domains the guest browsed are stored (log_domains is on) but never written to a log.
    for domain in BROWSED_DOMAINS:
        assert domain.lower().rstrip(".") not in blob.lower(), f"'{domain}' reached the log"
    # Nor does any key material or credential that passed through this session.
    assert secret_b64 not in blob
    assert guest_token not in blob
    # The session is identified by its id, which is exactly the administrative metadata allowed.
    assert session_id in blob


async def test_the_session_row_keeps_metadata_only_and_the_key_is_gone(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    """Section 15: administrative session data is persisted, browsing content is not."""
    await settings_service.update(db, SettingsPatch(log_domains=True))
    await db.commit()

    session_id, _, _ = await _drive_full_session(ws_connect, make_actor)
    db.expire_all()
    stored = await db.get(Session, uuid.UUID(session_id))
    assert stored is not None
    assert (stored.status, stored.end_reason) == ("ended", "guest_ended")
    assert (stored.bytes_up, stored.bytes_down) == (8192, 131072)
    assert await db.get(SessionKey, uuid.UUID(session_id)) is None, (
        "the key must not outlive the session"
    )

    domains = list(
        await db.scalars(
            select(SessionDomain.domain).where(SessionDomain.session_id == uuid.UUID(session_id))
        )
    )
    # Host names only, normalised - no scheme, no path, no query, nothing from a page.
    assert sorted(domains) == ["docs.example.com", "intranet.partner.example", "mail.example.com"]
    assert all(("/" not in d and "?" not in d and ":" not in d) for d in domains)


async def test_domains_are_not_stored_when_logging_is_off(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    """``log_domains`` defaults to false, and then not even the host names are kept."""
    session_id, _, _ = await _drive_full_session(ws_connect, make_actor)
    db.expire_all()
    rows = list(
        await db.scalars(
            select(SessionDomain).where(SessionDomain.session_id == uuid.UUID(session_id))
        )
    )
    assert rows == []
