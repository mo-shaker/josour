"""Presence, the host list and the reachability probe (docs/ws-protocol.md sections 4 and 6)."""

from typing import Any

import pytest
from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Presence
from app.services import presence as presence_service
from app.services.reachability_probe import ProbeResult
from tests.conftest import Actor, ActorFactory, WsFactory

HOSTS = "/api/v1/hosts"


def _headers(actor: Actor) -> dict[str, str]:
    return {"Authorization": f"Bearer {actor.token}"}


async def test_host_available_broadcasts_hosts_update_and_rest_agrees(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient
) -> None:
    guest = await make_actor("guest@example.com", display_name="Guest", device_name="GUEST-PC")
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host)
    await guest_ws.drain()

    await host_ws.send({"type": "host.available", "available": True, "listen_port": None})

    update = await guest_ws.receive()
    assert update == {
        "type": "hosts.update",
        "hosts": [
            {
                "device_id": host.device_id,
                "user_display_name": "Hosty",
                "device_name": "OFFICE-PC",
                "reachable": None,
            }
        ],
    }
    # The REST list is the same query, so it must return exactly the same rows.
    rest = await client.get(HOSTS, headers=_headers(guest))
    assert rest.json() == update["hosts"]

    # ... and the host never sees its own device.
    own = await host_ws.receive()
    assert own == {"type": "hosts.update", "hosts": []}
    assert (await client.get(HOSTS, headers=_headers(host))).json() == []


async def test_withdrawing_availability_empties_the_list(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient, db: AsyncSession
) -> None:
    guest = await make_actor("guest2@example.com")
    host = await make_actor("host2@example.com", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host)

    await host_ws.send({"type": "host.available", "available": True, "listen_port": 44300})
    await guest_ws.drain(timeout=0.1)
    assert len((await client.get(HOSTS, headers=_headers(guest))).json()) == 1

    await host_ws.send({"type": "host.available", "available": False})
    update = await guest_ws.expect("hosts.update", skip=frozenset())
    assert update["hosts"] == []
    assert (await client.get(HOSTS, headers=_headers(guest))).json() == []

    await db.rollback()
    row = await db.get(Presence, host.device.id)
    assert row is not None
    assert (row.is_available_host, row.listen_port, row.reachable) == (False, None, None)


async def test_host_disappears_from_the_list_when_it_disconnects(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient
) -> None:
    guest = await make_actor("guest3@example.com")
    host = await make_actor("host3@example.com", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host)
    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.drain(timeout=0.1)

    await host_ws.disconnect()

    assert (await guest_ws.expect("hosts.update", skip=frozenset()))["hosts"] == []
    assert (await client.get(HOSTS, headers=_headers(guest))).json() == []


async def test_probe_runs_in_the_background_and_republishes_the_list(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    monkeypatch: pytest.MonkeyPatch,
    db: AsyncSession,
) -> None:
    probed: list[tuple[str, int, float]] = []

    async def fake_probe(ip: str, port: int, timeout: float) -> ProbeResult:
        probed.append((ip, port, timeout))
        return ProbeResult(reachable=True, latency_ms=12)

    monkeypatch.setattr(presence_service, "tcp_probe", fake_probe)

    guest = await make_actor("guest4@example.com")
    host = await make_actor("host4@example.com", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    # A globally routable peer, otherwise the probe is skipped like POST /probe would.
    host_ws = await ws_connect(host, client=("93.184.216.34", 51000))
    await guest_ws.drain()

    await host_ws.send({"type": "host.available", "available": True, "listen_port": 44300})

    first = await guest_ws.receive()
    assert first["hosts"][0]["reachable"] is None, "the handler does not wait for the probe"
    second = await guest_ws.receive()
    assert second["hosts"][0]["reachable"] is True

    assert probed == [("93.184.216.34", 44300, 3.0)]
    await db.rollback()
    row = await db.get(Presence, host.device.id)
    assert row is not None and row.reachable is True


async def test_private_addresses_are_never_probed(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    monkeypatch: pytest.MonkeyPatch,
    db: AsyncSession,
) -> None:
    async def fail_probe(*_: Any, **__: Any) -> ProbeResult:  # pragma: no cover - must not run
        raise AssertionError("a private address must not be probed")

    monkeypatch.setattr(presence_service, "tcp_probe", fail_probe)

    host = await make_actor("host5@example.com", device_name="OFFICE-PC")
    host_ws = await ws_connect(host, client=("192.168.1.20", 51000))
    await host_ws.send({"type": "host.available", "available": True, "listen_port": 44300})
    await host_ws.drain(timeout=0.2)

    await db.rollback()
    row = await db.get(Presence, host.device.id)
    assert row is not None and row.reachable is None


async def test_hosts_exclude_your_own_devices(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    client: AsyncClient,
    make_device: Any,
    db: AsyncSession,
) -> None:
    """An available device of your own is a host for everybody except you."""
    actor = await make_actor("solo@example.com", display_name="Solo", device_name="LAPTOP")
    other = await make_actor("other@example.com", display_name="Other", device_name="THEIRS")
    second_device = await make_device(actor.user, "DESKTOP")
    await db.commit()

    ws = await ws_connect(actor)
    other_ws = await ws_connect(other)
    await other_ws.drain(timeout=0.1)
    await ws.send({"type": "host.available", "available": True})
    await ws.drain(timeout=0.1)

    assert (await client.get(HOSTS, headers=_headers(actor))).json() == []
    assert ws.snapshot == {"type": "hosts.snapshot", "hosts": []}
    listed = (await client.get(HOSTS, headers=_headers(other))).json()
    assert [row["device_name"] for row in listed] == ["LAPTOP"]
    assert (await other_ws.expect("hosts.update", skip=frozenset()))["hosts"] == listed
    assert second_device.id != actor.device.id
