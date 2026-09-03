"""Applies the Alembic migrations to a fresh PostgreSQL database and checks for model drift.

Runs only when DATABASE_URL_TEST points at PostgreSQL; the throw-away database is created next
to it and dropped afterwards.
"""

import asyncio
import os
import subprocess
import sys
import uuid
from collections.abc import Iterator
from pathlib import Path

import pytest
from sqlalchemy.engine import make_url

BACKEND_DIR = Path(__file__).resolve().parents[1]
TEST_URL = os.environ.get("DATABASE_URL_TEST", "")

pytestmark = pytest.mark.skipif(
    not TEST_URL.startswith("postgresql"), reason="DATABASE_URL_TEST is not PostgreSQL"
)


async def _admin_exec(sql: str) -> None:
    import asyncpg

    url = make_url(TEST_URL)
    conn = await asyncpg.connect(
        host=url.host,
        port=url.port or 5432,
        user=url.username,
        password=url.password,
        database=url.database,
    )
    try:
        await conn.execute(sql)
    finally:
        await conn.close()


@pytest.fixture
def migration_db_url() -> Iterator[str]:
    name = f"rb_mig_{uuid.uuid4().hex[:8]}"
    asyncio.run(_admin_exec(f'CREATE DATABASE "{name}"'))
    try:
        yield make_url(TEST_URL).set(database=name).render_as_string(hide_password=False)
    finally:
        asyncio.run(_admin_exec(f'DROP DATABASE "{name}"'))


def _alembic(*args: str, url: str) -> subprocess.CompletedProcess[str]:
    env = {**os.environ, "DATABASE_URL": url}
    return subprocess.run(
        [sys.executable, "-m", "alembic", *args],
        cwd=BACKEND_DIR,
        env=env,
        capture_output=True,
        text=True,
        check=False,
    )


def test_migrations_apply_and_match_models(migration_db_url: str) -> None:
    upgrade = _alembic("upgrade", "head", url=migration_db_url)
    assert upgrade.returncode == 0, upgrade.stderr

    check = _alembic("check", url=migration_db_url)
    assert check.returncode == 0, check.stdout + check.stderr
    assert "No new upgrade operations detected" in check.stdout + check.stderr

    downgrade = _alembic("downgrade", "base", url=migration_db_url)
    assert downgrade.returncode == 0, downgrade.stderr

    upgrade_again = _alembic("upgrade", "head", url=migration_db_url)
    assert upgrade_again.returncode == 0, upgrade_again.stderr
