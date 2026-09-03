import pytest
from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession
from typer.testing import CliRunner

from app.cli import cli
from app.core.errors import ApiError
from app.services import allowlist

DOMAINS = "/api/v1/domains"


async def test_domains_empty_is_version_zero(client: AsyncClient, auth_headers: dict) -> None:
    response = await client.get(DOMAINS, headers=auth_headers)
    assert response.status_code == 200
    assert response.json() == {"version": 0, "entries": []}
    assert response.headers["etag"] == '"0"'


async def test_domains_requires_auth(client: AsyncClient) -> None:
    assert (await client.get(DOMAINS)).status_code == 401


async def test_add_domain_publishes_versions_with_etag(
    client: AsyncClient, auth_headers: dict, db: AsyncSession
) -> None:
    await allowlist.add_domain(db, "example.com")
    await db.commit()

    response = await client.get(DOMAINS, headers=auth_headers)
    assert response.status_code == 200
    assert response.json() == {"version": 1, "entries": ["example.com"]}
    assert response.headers["etag"] == '"1"'

    cached = await client.get(DOMAINS, headers={**auth_headers, "If-None-Match": '"1"'})
    assert cached.status_code == 304 and cached.content == b""
    assert cached.headers["etag"] == '"1"'

    weak = await client.get(DOMAINS, headers={**auth_headers, "If-None-Match": 'W/"1"'})
    assert weak.status_code == 304

    stale = await client.get(DOMAINS, headers={**auth_headers, "If-None-Match": '"0"'})
    assert stale.status_code == 200

    specific = await client.get(DOMAINS, params={"version": 1}, headers=auth_headers)
    assert specific.status_code == 200 and specific.json()["version"] == 1

    missing = await client.get(DOMAINS, params={"version": 2}, headers=auth_headers)
    assert missing.status_code == 404
    assert missing.json()["error"]["code"] == "not_found"

    await allowlist.add_domain(db, "=exact.com")
    await allowlist.add_domain(db, "portal.corp:8443")
    await db.commit()
    latest = await client.get(DOMAINS, headers=auth_headers)
    assert latest.json() == {
        "version": 3,
        "entries": ["example.com", "=exact.com", "portal.corp:8443"],
    }
    older = await client.get(DOMAINS, params={"version": 1}, headers=auth_headers)
    assert older.json() == {"version": 1, "entries": ["example.com"]}


async def test_add_duplicate_domain_conflicts(db: AsyncSession) -> None:
    await allowlist.add_domain(db, "example.com")
    with pytest.raises(ApiError) as excinfo:
        await allowlist.add_domain(db, "example.com")
    assert excinfo.value.code == "conflict"


@pytest.mark.parametrize(
    "entry",
    [
        "example.com",
        "=exact.com",
        "portal.corp:8443",
        "=a.b.c:443",
        "xn--80ak6aa92e.com",
        "corp:80",
    ],
)
def test_validate_entry_accepts(entry: str) -> None:
    assert allowlist.validate_entry(f" {entry} ") == entry


@pytest.mark.parametrize(
    "entry",
    [
        "",
        "   ",
        "*",
        "*.example.com",
        "example.com/path",
        "example .com",
        "Example.com",
        "example.com:0",
        "example.com:70000",
        "example.com:08",
        "example.com:",
        "=",
        "10.0.0.1",
        "-bad.com",
        "bad-.com",
        "a..b",
        "http://example.com",
    ],
)
def test_validate_entry_rejects(entry: str) -> None:
    with pytest.raises(ApiError) as excinfo:
        allowlist.validate_entry(entry)
    assert excinfo.value.code == "validation_error"


def test_cli_add_and_list_domains() -> None:
    runner = CliRunner()
    added = runner.invoke(cli, ["add-domain", "cli.example.com"])
    assert added.exit_code == 0, added.output
    assert "version 1" in added.output

    listed = runner.invoke(cli, ["list-domains"])
    assert listed.exit_code == 0
    assert listed.output.splitlines() == ["version 1", "cli.example.com"]

    rejected = runner.invoke(cli, ["add-domain", "bad*entry"])
    assert rejected.exit_code == 1
    assert "error:" in rejected.output


def test_cli_create_users() -> None:
    runner = CliRunner()
    admin = runner.invoke(
        cli,
        ["create-admin", "--email", "root@example.com", "--password", "pw-12345678"],
    )
    assert admin.exit_code == 0, admin.output
    assert "created admin root@example.com" in admin.output

    user = runner.invoke(
        cli,
        [
            "create-user",
            "--email",
            "u@example.com",
            "--password",
            "pw-12345678",
            "--display-name",
            "U",
        ],
    )
    assert user.exit_code == 0, user.output
    assert "created user u@example.com" in user.output

    duplicate = runner.invoke(
        cli, ["create-user", "--email", "u@example.com", "--password", "pw-12345678"]
    )
    assert duplicate.exit_code == 1
    assert "already exists" in duplicate.output
