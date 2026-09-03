import uuid
from typing import Any

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Device, RefreshToken, SecurityEvent
from app.models.enums import DeviceStatus

REFRESH = "/api/v1/auth/refresh"
LOGOUT = "/api/v1/auth/logout"


async def test_refresh_rotates_token(client: AsyncClient, logged_in: dict[str, Any]) -> None:
    response = await client.post(REFRESH, json={"refresh_token": logged_in["refresh_token"]})
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["refresh_token"] != logged_in["refresh_token"]
    assert body["device"] == {"id": logged_in["device"]["id"], "name": "LAPTOP-01", "secret": None}
    assert body["user"] == logged_in["user"]

    me = await client.get("/api/v1/me", headers={"Authorization": f"Bearer {body['access_token']}"})
    assert me.status_code == 200


async def test_reusing_rotated_token_revokes_chain(
    client: AsyncClient, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    old = logged_in["refresh_token"]
    rotated = await client.post(REFRESH, json={"refresh_token": old})
    new = rotated.json()["refresh_token"]

    reuse = await client.post(REFRESH, json={"refresh_token": old})
    assert reuse.status_code == 401
    assert reuse.json()["error"]["code"] == "unauthorized"

    with_new = await client.post(REFRESH, json={"refresh_token": new})
    assert with_new.status_code == 401

    live = await db.scalar(select(RefreshToken).where(RefreshToken.revoked_at.is_(None)).limit(1))
    assert live is None
    reuse_event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == "refresh_reuse")
    )
    assert reuse_event is not None and str(reuse_event.device_id) == logged_in["device"]["id"]


async def test_refresh_unknown_token(client: AsyncClient) -> None:
    response = await client.post(REFRESH, json={"refresh_token": "definitely-not-a-token"})
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "unauthorized"


async def test_logout_revokes_refresh_token(
    client: AsyncClient, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    token = logged_in["refresh_token"]
    response = await client.post(LOGOUT, json={"refresh_token": token})
    assert response.status_code == 204 and response.content == b""

    after = await client.post(REFRESH, json={"refresh_token": token})
    assert after.status_code == 401

    again = await client.post(LOGOUT, json={"refresh_token": token})
    assert again.status_code == 204
    assert await db.scalar(select(SecurityEvent).where(SecurityEvent.type == "logout"))


async def test_refresh_with_revoked_device(
    client: AsyncClient, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    device = await db.get(Device, uuid.UUID(logged_in["device"]["id"]))
    assert device is not None
    device.status = DeviceStatus.REVOKED
    await db.commit()
    response = await client.post(REFRESH, json={"refresh_token": logged_in["refresh_token"]})
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "device_revoked"
