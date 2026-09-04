from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import SecurityEvent
from tests.conftest import LoginBody

EVENTS = "/api/v1/admin/security-events"
EVENT_KEYS = {"id", "type", "user_id", "device_id", "ip", "details", "created_at"}


async def test_list_security_events_newest_first_with_filters(
    client: AsyncClient,
    admin_headers: dict,
    admin_logged_in: dict[str, Any],
    login_body: LoginBody,
    db: AsyncSession,
) -> None:
    failed = await client.post("/api/v1/auth/login", json=login_body(email="nobody@example.com"))
    assert failed.status_code == 401
    db.add(
        SecurityEvent(
            type="logout",
            ip="203.0.113.5",
            details={"note": "old"},
            created_at=utcnow() - timedelta(days=1),
        )
    )
    await db.commit()

    response = await client.get(EVENTS, headers=admin_headers)
    assert response.status_code == 200
    rows = response.json()
    assert [r["type"] for r in rows] == ["login_failed", "login_success", "logout"]
    assert all(set(r) == EVENT_KEYS for r in rows)
    assert rows[1]["user_id"] == admin_logged_in["user"]["id"]
    assert rows[1]["device_id"] == admin_logged_in["device"]["id"]
    assert rows[1]["ip"] == "127.0.0.1" and rows[1]["created_at"].endswith("Z")
    assert rows[0]["details"] == {"reason": "unknown_user", "email": "nobody@example.com"}
    assert rows[2]["details"] == {"note": "old"}

    by_type = await client.get(EVENTS, params={"type": "logout"}, headers=admin_headers)
    assert [r["type"] for r in by_type.json()] == ["logout"]
    limited = await client.get(EVENTS, params={"limit": 1}, headers=admin_headers)
    assert [r["type"] for r in limited.json()] == ["login_failed"]
    assert (
        await client.get(EVENTS, params={"limit": 501}, headers=admin_headers)
    ).status_code == 422
    assert (
        await client.get(EVENTS, params={"type": "nothing"}, headers=admin_headers)
    ).json() == []
