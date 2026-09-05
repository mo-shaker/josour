import json
import uuid
from typing import Any

import pytest
from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectDiagnostic, Device, SecurityEvent, User
from app.models.enums import SecurityEventType, SessionStatus
from app.services.diagnostics import (
    MAX_DATA_BYTES,
    MAX_REPORTED_COUNT,
    MAX_REPORTED_PEERS,
    data_size_bytes,
)
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


# ------------------------------------- the reserved `listener_unauthenticated` key (docs/api.md)


async def _events(db: AsyncSession) -> list[SecurityEvent]:
    return list(
        await db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.type == SecurityEventType.LISTENER_UNAUTHENTICATED
            )
        )
    )


async def test_listener_unauthenticated_raises_a_security_event_beside_the_diagnostics_row(
    client: AsyncClient, auth_headers: dict, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    payload = {
        "listener_unauthenticated": 3,
        "listener_port": 51820,
        "unauthenticated_peers": ["198.51.100.4", "203.0.113.9"],
        "note": "connect window closed",
    }
    response = await client.post(
        DIAGNOSTICS, json={"role": "host", "data": payload}, headers=auth_headers
    )
    assert response.status_code == 201, response.text

    # The normal diagnostics row is stored unchanged...
    row = await db.get(ConnectDiagnostic, uuid.UUID(response.json()["id"]))
    assert row is not None and row.data == payload

    # ...and the signal is raised alongside it, for the calling device.
    events = await _events(db)
    assert len(events) == 1
    assert str(events[0].device_id) == logged_in["device"]["id"]
    assert events[0].details == {
        "count": 3,
        "listener_port": 51820,
        "peers": ["198.51.100.4", "203.0.113.9"],
    }


async def test_listener_signal_records_a_count_and_addresses_and_nothing_else(
    client: AsyncClient, auth_headers: dict, db: AsyncSession
) -> None:
    """No payloads and no domain names ever reach ``security_events`` (privacy 15.12): anything
    that is not an IP address is dropped, the list is capped at ten, and the count is clamped."""
    peers = [f"198.51.100.{i}" for i in range(1, 16)]
    response = await client.post(
        DIAGNOSTICS,
        json={
            "data": {
                "listener_unauthenticated": 10**9,
                "listener_port": 70000,  # out of range: ignored, not stored
                "unauthenticated_peers": [
                    "internal.corp",  # a name, not an address
                    "not-an-ip",
                    "198.51.100.4",
                    "198.51.100.4",  # duplicate
                    "::FFFF:0:0",  # canonicalised
                    *peers,
                ],
                "url": "https://example.com/secret?token=abc",
            }
        },
        headers=auth_headers,
    )
    assert response.status_code == 201

    details = (await _events(db))[0].details
    assert details is not None
    assert set(details) == {"count", "peers"}
    assert details["count"] == MAX_REPORTED_COUNT
    assert len(details["peers"]) == MAX_REPORTED_PEERS
    assert details["peers"][:2] == ["198.51.100.4", "::ffff:0:0"]
    serialised = json.dumps(details)
    assert "internal.corp" not in serialised and "example.com" not in serialised


@pytest.mark.parametrize(
    "value",
    [True, False, 0, -1, "3", None, [3], {"count": 3}],
)
async def test_only_a_positive_number_raises_the_signal(
    client: AsyncClient, auth_headers: dict, db: AsyncSession, value: Any
) -> None:
    """``True`` is an ``int`` in Python and ``data`` is free-form, so the guard is explicit."""
    response = await client.post(
        DIAGNOSTICS, json={"data": {"listener_unauthenticated": value}}, headers=auth_headers
    )
    assert response.status_code == 201
    assert await _events(db) == []


async def test_ordinary_diagnostics_raise_no_security_event(
    client: AsyncClient, auth_headers: dict, db: AsyncSession
) -> None:
    response = await client.post(
        DIAGNOSTICS,
        json={"data": {"upnp_found": True, "listener_port": 51820}},
        headers=auth_headers,
    )
    assert response.status_code == 201
    assert await _events(db) == []


async def test_the_signal_is_visible_to_an_administrator(
    client: AsyncClient, auth_headers: dict, admin_headers: dict
) -> None:
    posted = await client.post(
        DIAGNOSTICS,
        json={"data": {"listener_unauthenticated": 2, "unauthenticated_peers": ["203.0.113.9"]}},
        headers=auth_headers,
    )
    assert posted.status_code == 201

    listed = await client.get(
        "/api/v1/admin/security-events?type=listener_unauthenticated", headers=admin_headers
    )
    assert listed.status_code == 200
    body = listed.json()
    assert len(body) == 1
    assert body[0]["type"] == "listener_unauthenticated"
    assert body[0]["details"] == {"count": 2, "peers": ["203.0.113.9"]}
