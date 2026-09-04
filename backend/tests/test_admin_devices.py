import uuid
from typing import Any

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Device, SecurityEvent, User
from tests.conftest import DeviceFactory

DEVICES = "/api/v1/admin/devices"
DEVICE_KEYS = {
    "id",
    "user_id",
    "name",
    "os_version",
    "os_build",
    "status",
    "last_seen_at",
    "created_at",
}


async def test_list_devices_all_and_by_user(
    client: AsyncClient,
    admin_headers: dict,
    admin: User,
    user: User,
    logged_in: dict[str, Any],
    make_device: DeviceFactory,
    db: AsyncSession,
) -> None:
    other = await make_device(user, "DESKTOP-02")
    await db.commit()

    everything = await client.get(DEVICES, headers=admin_headers)
    assert everything.status_code == 200
    rows = everything.json()
    assert {d["name"] for d in rows} == {"LAPTOP-01", "DESKTOP-02", "ADMIN-PC"}
    assert all(set(d) == DEVICE_KEYS for d in rows)
    assert "secret" not in everything.text and "hash" not in everything.text

    mine = await client.get(DEVICES, params={"user_id": str(user.id)}, headers=admin_headers)
    assert {d["id"] for d in mine.json()} == {logged_in["device"]["id"], str(other.id)}
    assert all(d["user_id"] == str(user.id) for d in mine.json())

    nobody = await client.get(DEVICES, params={"user_id": str(uuid.uuid4())}, headers=admin_headers)
    assert nobody.json() == []
    assert (
        await client.get(DEVICES, params={"user_id": "nope"}, headers=admin_headers)
    ).status_code == 422


async def test_revoke_device_is_idempotent_and_revokes_tokens(
    client: AsyncClient,
    admin_headers: dict,
    admin: User,
    logged_in: dict[str, Any],
    auth_headers: dict,
    db: AsyncSession,
) -> None:
    device_id = logged_in["device"]["id"]
    response = await client.post(f"{DEVICES}/{device_id}/revoke", headers=admin_headers)
    assert response.status_code == 204 and response.content == b""

    device = await db.get(Device, uuid.UUID(device_id))
    assert device is not None and device.status == "revoked"
    assert (await client.get("/api/v1/me", headers=auth_headers)).status_code == 403
    refreshed = await client.post(
        "/api/v1/auth/refresh", json={"refresh_token": logged_in["refresh_token"]}
    )
    assert refreshed.status_code == 401

    again = await client.post(f"{DEVICES}/{device_id}/revoke", headers=admin_headers)
    assert again.status_code == 204

    events = (
        await db.scalars(select(SecurityEvent).where(SecurityEvent.type == "device_revoked"))
    ).all()
    assert len(events) == 1
    assert str(events[0].device_id) == device_id and events[0].user_id == device.user_id
    assert events[0].details == {"by": "admin", "actor_user_id": str(admin.id)}
    assert events[0].ip == "127.0.0.1"

    listed = await client.get(
        DEVICES, params={"user_id": str(device.user_id)}, headers=admin_headers
    )
    assert listed.json()[0]["status"] == "revoked"


async def test_revoke_unknown_device(client: AsyncClient, admin_headers: dict) -> None:
    response = await client.post(f"{DEVICES}/{uuid.uuid4()}/revoke", headers=admin_headers)
    assert response.status_code == 404 and response.json()["error"]["code"] == "not_found"


async def test_owner_revocation_still_records_owner_event(
    client: AsyncClient, logged_in: dict[str, Any], auth_headers: dict, db: AsyncSession
) -> None:
    device_id = logged_in["device"]["id"]
    assert (
        await client.delete(f"/api/v1/me/devices/{device_id}", headers=auth_headers)
    ).status_code == 204
    event = await db.scalar(select(SecurityEvent).where(SecurityEvent.type == "device_revoked"))
    assert event is not None and event.details == {"by": "owner"}
