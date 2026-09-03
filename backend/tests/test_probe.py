import asyncio
from collections.abc import AsyncIterator

import pytest
from httpx import AsyncClient

from app.api.routers import probe as probe_router
from app.services.reachability_probe import forbidden_target_reason, tcp_probe

PROBE = "/api/v1/probe"


@pytest.fixture
async def listener() -> AsyncIterator[int]:
    async def handle(_: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        writer.close()

    server = await asyncio.start_server(handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    async with server:
        yield port


@pytest.fixture
async def closed_port() -> int:
    server = await asyncio.start_server(lambda r, w: None, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    server.close()
    await server.wait_closed()
    return port


async def test_tcp_probe_service(listener: int, closed_port: int) -> None:
    ok = await tcp_probe("127.0.0.1", listener)
    assert ok.reachable is True and isinstance(ok.latency_ms, int) and ok.latency_ms >= 0
    bad = await tcp_probe("127.0.0.1", closed_port)
    assert bad.reachable is False and bad.latency_ms is None


async def test_probe_endpoint_reachable_and_unreachable(
    client: AsyncClient,
    auth_headers: dict,
    listener: int,
    closed_port: int,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # The endpoint refuses loopback targets; allow them here to exercise the real socket path.
    monkeypatch.setattr(probe_router, "forbidden_target_reason", lambda ip: None)

    ok = await client.post(PROBE, json={"ip": "127.0.0.1", "port": listener}, headers=auth_headers)
    assert ok.status_code == 200, ok.text
    body = ok.json()
    assert body["reachable"] is True and isinstance(body["latency_ms"], int)

    bad = await client.post(
        PROBE, json={"ip": "127.0.0.1", "port": closed_port}, headers=auth_headers
    )
    assert bad.status_code == 200
    assert bad.json() == {"reachable": False, "latency_ms": None}


@pytest.mark.parametrize(
    "ip",
    [
        "127.0.0.1",
        "10.1.2.3",
        "192.168.1.1",
        "169.254.1.1",
        "100.64.0.1",
        "0.0.0.0",
        "224.0.0.1",
        "::1",
        "::",
        "fe80::1",
        "fc00::1",
        "::ffff:192.168.1.1",
    ],
)
async def test_probe_rejects_non_public_targets(
    client: AsyncClient, auth_headers: dict, ip: str
) -> None:
    response = await client.post(PROBE, json={"ip": ip, "port": 443}, headers=auth_headers)
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "validation_error"


def test_forbidden_target_reason_allows_public() -> None:
    assert forbidden_target_reason("8.8.8.8") is None
    assert forbidden_target_reason("2001:4860:4860::8888") is None
    assert forbidden_target_reason("::ffff:10.0.0.1") is not None


async def test_probe_validation_errors(client: AsyncClient, auth_headers: dict) -> None:
    bad_ip = await client.post(PROBE, json={"ip": "not-an-ip", "port": 80}, headers=auth_headers)
    assert bad_ip.status_code == 422 and bad_ip.json()["error"]["code"] == "validation_error"
    bad_port = await client.post(PROBE, json={"ip": "8.8.8.8", "port": 70000}, headers=auth_headers)
    assert bad_port.status_code == 422


async def test_probe_requires_auth(client: AsyncClient) -> None:
    assert (await client.post(PROBE, json={"ip": "8.8.8.8", "port": 443})).status_code == 401
