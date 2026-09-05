"""``manage.py list-sessions`` / ``end-session`` (plan section 3).

The CLI builds its own engine from ``DATABASE_URL`` (pointed at the test database by the
``_schema`` fixture) and runs outside the API process, so these tests assert what that path can
guarantee on its own: the row, the deleted ``session_keys`` secret and the audit trail.
"""

import asyncio
import uuid
from typing import Any

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from typer.testing import CliRunner

from app.cli import cli
from app.models import Device, SecurityEvent, Session, SessionKey, User
from app.models.enums import SecurityEventType, SessionEndReason, SessionStatus
from tests.conftest import DeviceFactory, SessionFactory

runner = CliRunner()


async def invoke(*args: str) -> Any:
    """The CLI owns its own ``asyncio.run``, so it must not be called on the test's loop."""
    return await asyncio.to_thread(runner.invoke, cli, list(args))


@pytest.fixture
async def pair(
    db: AsyncSession, user: User, admin: User, make_device: DeviceFactory
) -> tuple[User, Device, User, Device]:
    guest_device = await make_device(user, "GUEST-PC")
    host_device = await make_device(admin, "OFFICE-PC")
    await db.commit()
    return user, guest_device, admin, host_device


async def test_list_sessions_renders_rows_and_filters_by_status(
    db: AsyncSession, pair: tuple[User, Device, User, Device], make_session: SessionFactory
) -> None:
    guest, guest_device, host, host_device = pair
    live = await make_session(guest, guest_device, host, host_device)

    listed = await invoke("list-sessions")
    assert listed.exit_code == 0, listed.output
    assert str(live.id) in listed.output
    assert "GUEST-PC" in listed.output and "OFFICE-PC" in listed.output

    ended_only = await invoke("list-sessions", "--status", "ended")
    assert ended_only.exit_code == 0, ended_only.output
    assert ended_only.output.strip() == "no sessions"

    active_only = await invoke("list-sessions", "--status", "active", "--limit", "1")
    assert active_only.exit_code == 0 and str(live.id) in active_only.output


async def test_end_session_ends_the_row_deletes_the_key_and_audits(
    db: AsyncSession, pair: tuple[User, Device, User, Device], make_session: SessionFactory
) -> None:
    guest, guest_device, host, host_device = pair
    live = await make_session(guest, guest_device, host, host_device)
    session_id = live.id
    db.add(SessionKey(session_id=session_id, secret=b"x" * 32))
    await db.commit()

    result = await invoke("end-session", str(session_id))
    assert result.exit_code == 0, result.output
    assert "session key deleted" in result.output

    db.expire_all()
    reloaded = await db.get(Session, session_id)
    assert reloaded is not None
    assert reloaded.status == SessionStatus.ENDED
    assert reloaded.end_reason == SessionEndReason.ADMIN_TERMINATED
    assert await db.get(SessionKey, session_id) is None, (
        "the tunnel secret must not outlive the end"
    )

    event = await db.scalar(
        select(SecurityEvent).where(
            SecurityEvent.type == SecurityEventType.SESSION_ADMIN_TERMINATED
        )
    )
    assert event is not None
    assert event.user_id is None and event.details is not None
    assert event.details["via"] == "cli"
    assert event.details["session_id"] == str(session_id)


async def test_end_session_reports_unknown_and_already_ended(
    db: AsyncSession, pair: tuple[User, Device, User, Device], make_session: SessionFactory
) -> None:
    guest, guest_device, host, host_device = pair
    missing = await invoke("end-session", str(uuid.uuid4()))
    assert missing.exit_code == 1 and "error:" in missing.output

    done = await make_session(guest, guest_device, host, host_device, status=SessionStatus.ENDED)
    repeated = await invoke("end-session", str(done.id))
    assert repeated.exit_code == 1 and "already ended" in repeated.output
