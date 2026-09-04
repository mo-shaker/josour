from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import AppSetting
from app.schemas.settings import SettingsPatch
from app.services.app_settings import SettingsService, settings_service

SETTINGS = "/api/v1/admin/settings"
DEFAULTS = {
    "max_session_minutes": 120,
    "request_timeout_seconds": 60,
    "connect_timeout_seconds": 30,
    "log_domains": False,
    "allowed_ports": [80, 443],
}


async def test_get_settings_defaults(client: AsyncClient, admin_headers: dict) -> None:
    response = await client.get(SETTINGS, headers=admin_headers)
    assert response.status_code == 200 and response.json() == DEFAULTS


async def test_patch_settings_persists_and_merges(
    client: AsyncClient, admin_headers: dict, db: AsyncSession
) -> None:
    response = await client.patch(
        SETTINGS,
        json={"max_session_minutes": 45, "allowed_ports": [443, 8443]},
        headers=admin_headers,
    )
    assert response.status_code == 200, response.text
    assert response.json() == {**DEFAULTS, "max_session_minutes": 45, "allowed_ports": [443, 8443]}

    rows = {row.key: row.value for row in await db.scalars(select(AppSetting))}
    assert rows == {"max_session_minutes": 45, "allowed_ports": [443, 8443]}

    again = await client.patch(
        SETTINGS, json={"log_domains": True, "max_session_minutes": 60}, headers=admin_headers
    )
    assert again.json() == {
        **DEFAULTS,
        "max_session_minutes": 60,
        "allowed_ports": [443, 8443],
        "log_domains": True,
    }
    fetched = await client.get(SETTINGS, headers=admin_headers)
    assert fetched.json() == again.json()

    fresh = SettingsService()  # cold cache reads the committed rows
    loaded = await fresh.get(db)
    assert loaded.max_session_minutes == 60 and loaded.log_domains is True
    assert loaded.allowed_ports == [443, 8443]

    unchanged = await client.patch(SETTINGS, json={}, headers=admin_headers)
    assert unchanged.status_code == 200 and unchanged.json() == fetched.json()


async def test_patch_settings_validation(client: AsyncClient, admin_headers: dict) -> None:
    for body in (
        {"max_session_minutes": 0},
        {"max_session_minutes": 1441},
        {"request_timeout_seconds": 9},
        {"request_timeout_seconds": 301},
        {"connect_timeout_seconds": 5},
        {"allowed_ports": []},
        {"allowed_ports": [0]},
        {"allowed_ports": [70000]},
        {"allowed_ports": [80, 80]},
        {"log_domains": "yes please"},
        {"unknown_key": 1},
    ):
        response = await client.patch(SETTINGS, json=body, headers=admin_headers)
        assert response.status_code == 422, body
        assert response.json()["error"]["code"] == "validation_error"
    assert (await client.get(SETTINGS, headers=admin_headers)).json() == DEFAULTS


async def test_settings_service_cache_is_invalidated_on_update(db: AsyncSession) -> None:
    first = await settings_service.get(db)
    assert first.max_session_minutes == 120

    db.add(AppSetting(key="max_session_minutes", value=30))
    await db.commit()
    assert (await settings_service.get(db)).max_session_minutes == 120  # served from cache

    settings_service.invalidate()
    assert (await settings_service.get(db)).max_session_minutes == 30

    updated = await settings_service.update(db, SettingsPatch(request_timeout_seconds=90))
    assert updated.request_timeout_seconds == 90 and updated.max_session_minutes == 30
    assert (await settings_service.get(db)) == updated


async def test_settings_service_ignores_invalid_rows(db: AsyncSession) -> None:
    db.add(AppSetting(key="max_session_minutes", value="lots"))
    db.add(AppSetting(key="allowed_ports", value=[22]))
    db.add(AppSetting(key="not_a_setting", value=1))
    await db.commit()
    loaded = await SettingsService().get(db)
    assert loaded.max_session_minutes == 120 and loaded.allowed_ports == [22]
