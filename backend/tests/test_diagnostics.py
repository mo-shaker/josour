import uuid
from typing import Any

from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectDiagnostic, Device, User
from app.models.enums import SessionStatus
from app.services.diagnostics import MAX_DATA_BYTES, data_size_bytes
from app.services.users import create_user
from tests.conftest import DeviceFactory, SessionFactory

DIAGNOSTICS = "/api/v1/diagnostics"


async def test_post_diagnostics_stores_row_for_caller_device(
    client: AsyncClient, auth_headers: dict, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    payload = {
        "upnp_found": True,
        "mapping_ok": False,
        "candidates_tried": [{"type": "lan", "ms": 12}],
    }
    response = await client.post(DIAGNOSTICS, json={"data": payload}, headers=auth_headers)
    assert response.status_code == 201, response.text
    body = response.json()
    assert set(body) == {"id"}

    row = await db.get(ConnectDiagnostic, uuid.UUID(body["id"]))
    assert row is not None
    assert str(row.device_id) == logged_in["device"]["id"]
    assert row.session_id is None and row.role is None
    assert row.data == payload

    with_role = await client.post(
        DIAGNOSTICS,
        json={"session_id": None, "role": "host", "data": {"x": 1}},
        headers=auth_headers,
    )
    assert with_role.status_code == 201
    stored = await db.get(ConnectDiagnostic, uuid.UUID(with_role.json()["id"]))
    assert stored is not None and stored.role == "host"


async def test_post_diagnostics_with_session(
    client: AsyncClient,
    auth_headers: dict,
    user: User,
    logged_in: dict[str, Any],
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    my_device = await db.get(Device, uuid.UUID(logged_in["device"]["id"]))
    assert my_device is not None
    host = await create_user(db, email="host@example.com", password="pw-12345678", display_name="H")
    host_device = await make_device(host, "HOST-PC")
    a = await create_user(db, email="a@example.com", password="pw-12345678", display_name="A")
    b = await create_user(db, email="b@example.com", password="pw-12345678", display_name="B")
    a_device = await make_device(a, "A-PC")
    b_device = await make_device(b, "B-PC")
    await db.commit()
    mine = await make_session(user, my_device, host, host_device, status=SessionStatus.CONNECTING)
    theirs = await make_session(a, a_device, b, b_device, status=SessionStatus.CONNECTING)

    ok = await client.post(
        DIAGNOSTICS,
        json={"session_id": str(mine.id), "role": "guest", "data": {"e": "timeout"}},
        headers=auth_headers,
    )
    assert ok.status_code == 201
    row = await db.get(ConnectDiagnostic, uuid.UUID(ok.json()["id"]))
    assert row is not None and row.session_id == mine.id and row.role == "guest"

    foreign = await client.post(
        DIAGNOSTICS,
        json={"session_id": str(theirs.id), "role": "guest", "data": {}},
        headers=auth_headers,
    )
    assert foreign.status_code == 403 and foreign.json()["error"]["code"] == "forbidden"

    unknown = await client.post(
        DIAGNOSTICS, json={"session_id": str(uuid.uuid4()), "data": {}}, headers=auth_headers
    )
    assert unknown.status_code == 404 and unknown.json()["error"]["code"] == "not_found"


async def test_post_diagnostics_validation(client: AsyncClient, auth_headers: dict) -> None:
    too_big = {"blob": "x" * MAX_DATA_BYTES}
    assert data_size_bytes(too_big) > MAX_DATA_BYTES
    response = await client.post(DIAGNOSTICS, json={"data": too_big}, headers=auth_headers)
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "validation_error"
    assert "64 KB" in response.json()["error"]["message"]

    just_fits = {"blob": "x" * (MAX_DATA_BYTES - len('{"blob":""}'))}
    assert data_size_bytes(just_fits) == MAX_DATA_BYTES
    assert (
        await client.post(DIAGNOSTICS, json={"data": just_fits}, headers=auth_headers)
    ).status_code == 201

    for body in (
        {},
        {"data": "text"},
        {"data": [1]},
        {"data": {}, "role": "admin"},
        {"data": {}, "session_id": "x"},
    ):
        bad = await client.post(DIAGNOSTICS, json=body, headers=auth_headers)
        assert bad.status_code == 422, body


async def test_post_diagnostics_requires_auth(client: AsyncClient) -> None:
    assert (await client.post(DIAGNOSTICS, json={"data": {}})).status_code == 401
