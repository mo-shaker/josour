from httpx import AsyncClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import AllowedDomain, AllowlistVersion
from app.services import allowlist
from app.services.events import AllowlistPublished, event_bus

ADMIN_DOMAINS = "/api/v1/admin/domains"
DOMAINS = "/api/v1/domains"


async def test_get_domains_empty(client: AsyncClient, admin_headers: dict) -> None:
    response = await client.get(ADMIN_DOMAINS, headers=admin_headers)
    assert response.status_code == 200
    assert response.json() == {"version": 0, "entries": [], "updated_at": None}


async def test_put_domains_publishes_versions_and_event(
    client: AsyncClient, admin_headers: dict, auth_headers: dict, db: AsyncSession
) -> None:
    received: list[AllowlistPublished] = []
    unsubscribe = event_bus.subscribe(AllowlistPublished, received.append)
    try:
        first = await client.put(
            ADMIN_DOMAINS,
            json={"entries": ["example.com", "=exact.com", "portal.corp:8443"]},
            headers=admin_headers,
        )
        assert first.status_code == 200, first.text
        body = first.json()
        assert body["version"] == 1
        assert body["entries"] == ["example.com", "=exact.com", "portal.corp:8443"]
        assert body["updated_at"].endswith("Z")

        assert len(received) == 1
        assert received[0].version == 1
        assert received[0].entries == ("example.com", "=exact.com", "portal.corp:8443")
        assert received[0].updated_at.tzinfo is not None

        public = await client.get(DOMAINS, headers=auth_headers)
        assert public.json() == {
            "version": 1,
            "entries": ["example.com", "=exact.com", "portal.corp:8443"],
        }
        assert public.headers["etag"] == '"1"'

        second = await client.put(
            ADMIN_DOMAINS,
            json={"entries": ["portal.corp:8443", "new.example.org"]},
            headers=admin_headers,
        )
        assert second.status_code == 200
        assert second.json()["version"] == 2
        assert second.json()["entries"] == ["portal.corp:8443", "new.example.org"]
        assert [e.version for e in received] == [1, 2]

        current = await client.get(ADMIN_DOMAINS, headers=admin_headers)
        assert current.json() == second.json()
        older = await client.get(DOMAINS, params={"version": 1}, headers=auth_headers)
        assert older.json()["entries"] == ["example.com", "=exact.com", "portal.corp:8443"]

        rows = {row.entry: row.is_active for row in await db.scalars(select(AllowedDomain))}
        assert rows == {
            "example.com": False,
            "=exact.com": False,
            "portal.corp:8443": True,
            "new.example.org": True,
        }

        # Re-adding a deactivated entry re-activates the existing row (no duplicate).
        third = await client.put(
            ADMIN_DOMAINS, json={"entries": ["example.com"]}, headers=admin_headers
        )
        assert third.status_code == 200 and third.json()["version"] == 3
        assert await db.scalar(select(func.count()).select_from(AllowedDomain)) == 4
        latest = await db.get(AllowlistVersion, 3)
        assert latest is not None and latest.entries == ["example.com"]
        assert latest.created_by is not None
    finally:
        unsubscribe()


async def test_put_domains_empty_list_publishes_empty_version(
    client: AsyncClient, admin_headers: dict
) -> None:
    await client.put(ADMIN_DOMAINS, json={"entries": ["example.com"]}, headers=admin_headers)
    cleared = await client.put(ADMIN_DOMAINS, json={"entries": []}, headers=admin_headers)
    assert cleared.status_code == 200
    assert cleared.json()["version"] == 2 and cleared.json()["entries"] == []


async def test_put_domains_rejects_whole_request_listing_bad_entries(
    client: AsyncClient, admin_headers: dict, db: AsyncSession
) -> None:
    received: list[AllowlistPublished] = []
    unsubscribe = event_bus.subscribe(AllowlistPublished, received.append)
    try:
        response = await client.put(
            ADMIN_DOMAINS,
            json={"entries": ["good.com", "Bad.com", "*.wild.com", "good.com", "x.com:99999"]},
            headers=admin_headers,
        )
    finally:
        unsubscribe()
    assert response.status_code == 422, response.text
    error = response.json()["error"]
    assert error["code"] == "validation_error"
    for bad in ("'Bad.com'", "'*.wild.com'", "'x.com:99999'", "'good.com' (duplicate entry)"):
        assert bad in error["message"]
    assert error["message"].count("'good.com'") == 1  # only the duplicate is reported

    assert received == []
    assert await db.scalar(select(func.count()).select_from(AllowlistVersion)) == 0
    assert await db.scalar(select(func.count()).select_from(AllowedDomain)) == 0

    assert (await client.put(ADMIN_DOMAINS, json={}, headers=admin_headers)).status_code == 422
    assert (
        await client.put(ADMIN_DOMAINS, json={"entries": "x"}, headers=admin_headers)
    ).status_code == 422


async def test_put_domains_requires_admin(client: AsyncClient, auth_headers: dict) -> None:
    response = await client.put(ADMIN_DOMAINS, json={"entries": ["a.com"]}, headers=auth_headers)
    assert response.status_code == 403
    assert (await client.put(ADMIN_DOMAINS, json={"entries": ["a.com"]})).status_code == 401


def test_validate_entries_collects_all_errors() -> None:
    entries, errors = allowlist.validate_entries([" a.com ", "b.com", "a.com", "-x.com"])
    assert entries == ["a.com", "b.com"]
    assert [(e.entry, e.reason) for e in errors] == [
        ("a.com", "duplicate entry"),
        ("-x.com", "host must be a valid domain name"),
    ]


async def test_cli_add_domain_and_admin_put_share_validation(
    client: AsyncClient, admin_headers: dict, db: AsyncSession
) -> None:
    await allowlist.add_domain(db, "cli.example.com")
    await db.commit()
    current = await client.get(ADMIN_DOMAINS, headers=admin_headers)
    assert current.json()["version"] == 1 and current.json()["entries"] == ["cli.example.com"]
    replaced = await client.put(
        ADMIN_DOMAINS,
        json={"entries": ["cli.example.com", "api.example.com"]},
        headers=admin_headers,
    )
    assert replaced.json()["version"] == 2
