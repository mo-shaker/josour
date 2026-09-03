from typing import Any

from httpx import AsyncClient

from tests.conftest import LoginBody

ME = "/api/v1/me"


async def test_get_me(client: AsyncClient, logged_in: dict[str, Any], auth_headers: dict) -> None:
    response = await client.get(ME, headers=auth_headers)
    assert response.status_code == 200
    assert response.json() == logged_in["user"]


async def test_me_requires_bearer(client: AsyncClient) -> None:
    missing = await client.get(ME)
    assert missing.status_code == 401
    assert missing.json()["error"]["code"] == "unauthorized"

    bogus = await client.get(ME, headers={"Authorization": "Bearer nope"})
    assert bogus.status_code == 401
    assert bogus.json()["error"]["code"] == "unauthorized"


async def test_list_devices(
    client: AsyncClient, logged_in: dict[str, Any], auth_headers: dict, login_body: LoginBody
) -> None:
    second = await client.post("/api/v1/auth/login", json=login_body(name="DESKTOP-02"))
    assert second.status_code == 200

    response = await client.get(f"{ME}/devices", headers=auth_headers)
    assert response.status_code == 200
    devices = response.json()
    assert [d["name"] for d in devices] == ["LAPTOP-01", "DESKTOP-02"]
    for device in devices:
        assert set(device) == {"id", "name", "os_version", "status", "last_seen_at", "created_at"}
        assert device["status"] == "active"
        assert device["os_version"] == "Windows 11 Pro"
        assert device["last_seen_at"].endswith("Z") and device["created_at"].endswith("Z")


async def test_delete_device_revokes_it(
    client: AsyncClient, logged_in: dict[str, Any], auth_headers: dict, login_body: LoginBody
) -> None:
    device_id = logged_in["device"]["id"]
    response = await client.delete(f"{ME}/devices/{device_id}", headers=auth_headers)
    assert response.status_code == 204

    listed = await client.get(f"{ME}/devices", headers=auth_headers)
    assert listed.status_code == 403  # the caller's own device is now revoked
    assert listed.json()["error"]["code"] == "device_revoked"

    relogin = await client.post(
        "/api/v1/auth/login",
        json=login_body(device_id=device_id, secret=logged_in["device"]["secret"]),
    )
    assert relogin.status_code == 403
    assert relogin.json()["error"]["code"] == "device_revoked"

    refreshed = await client.post(
        "/api/v1/auth/refresh", json={"refresh_token": logged_in["refresh_token"]}
    )
    assert refreshed.status_code == 401


async def test_delete_unknown_or_foreign_device_is_not_found(
    client: AsyncClient, auth_headers: dict
) -> None:
    response = await client.delete(
        f"{ME}/devices/0d3c5d4a-9f1e-4d0b-8a1e-2f8a0c7b6e5d", headers=auth_headers
    )
    assert response.status_code == 404
    assert response.json()["error"]["code"] == "not_found"
