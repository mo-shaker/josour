"""Test fixtures.

Uses ``DATABASE_URL_TEST`` (PostgreSQL) when set, otherwise a throw-away SQLite file under
pytest's temp directory. Schema is created with ``metadata.create_all``; every table is emptied
after each test.
"""

import asyncio
import os
import uuid
from collections.abc import AsyncIterator, Callable, Iterator
from typing import Any

import pytest
from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession

os.environ.setdefault("JWT_SECRET", "test-only-secret-not-for-production-use")
os.environ.setdefault("ENV", "test")

from app.core.config import get_settings  # noqa: E402
from app.db.session import get_db, make_engine, make_sessionmaker  # noqa: E402
from app.models import Base, User  # noqa: E402
from app.services.users import create_user  # noqa: E402

PASSWORD = "correct-horse-battery"


@pytest.fixture(scope="session")
def database_url(tmp_path_factory: pytest.TempPathFactory) -> str:
    configured = os.environ.get("DATABASE_URL_TEST")
    if configured:
        return configured
    path = tmp_path_factory.mktemp("db") / "routebridge-test.sqlite"
    return f"sqlite+aiosqlite:///{path}"


async def _create_schema(url: str) -> None:
    engine = make_engine(url)
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    await engine.dispose()


async def _empty_tables(url: str) -> None:
    engine = make_engine(url)
    async with engine.begin() as conn:
        for table in reversed(Base.metadata.sorted_tables):
            await conn.execute(table.delete())
    await engine.dispose()


@pytest.fixture(scope="session", autouse=True)
def _schema(database_url: str) -> None:
    asyncio.run(_create_schema(database_url))
    # The CLI builds its own engine from settings; point it at the test database.
    os.environ["DATABASE_URL"] = database_url
    get_settings.cache_clear()


@pytest.fixture(autouse=True)
def _clean_tables(database_url: str, _schema: None) -> Iterator[None]:
    yield
    asyncio.run(_empty_tables(database_url))


@pytest.fixture
async def engine(database_url: str) -> AsyncIterator[AsyncEngine]:
    engine = make_engine(database_url)
    yield engine
    await engine.dispose()


@pytest.fixture
async def db(engine: AsyncEngine) -> AsyncIterator[AsyncSession]:
    async with make_sessionmaker(engine)() as session:
        yield session


@pytest.fixture
def app(engine: AsyncEngine) -> FastAPI:
    from app.main import create_app

    application = create_app()
    sessionmaker = make_sessionmaker(engine)

    async def override_get_db() -> AsyncIterator[AsyncSession]:
        async with sessionmaker() as session:
            yield session

    application.dependency_overrides[get_db] = override_get_db
    return application


@pytest.fixture
async def client(app: FastAPI) -> AsyncIterator[AsyncClient]:
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as c:
        yield c


@pytest.fixture
async def user(db: AsyncSession) -> User:
    created = await create_user(
        db, email="alice@example.com", password=PASSWORD, display_name="Alice"
    )
    await db.commit()
    return created


LoginBody = Callable[..., dict[str, Any]]


@pytest.fixture
def login_body() -> LoginBody:
    def build(
        email: str = "alice@example.com",
        password: str = PASSWORD,
        device_id: uuid.UUID | str | None = None,
        secret: str | None = None,
        name: str = "LAPTOP-01",
    ) -> dict[str, Any]:
        return {
            "email": email,
            "password": password,
            "device": {
                "id": str(device_id) if device_id else None,
                "secret": secret,
                "name": name,
                "os_version": "Windows 11 Pro",
                "os_build": "22631",
            },
        }

    return build


@pytest.fixture
async def logged_in(client: AsyncClient, user: User, login_body: LoginBody) -> dict[str, Any]:
    """First login: registers a device and returns the token payload (with device.secret)."""
    response = await client.post("/api/v1/auth/login", json=login_body())
    assert response.status_code == 200, response.text
    return response.json()


@pytest.fixture
def auth_headers(logged_in: dict[str, Any]) -> dict[str, str]:
    return {"Authorization": f"Bearer {logged_in['access_token']}"}
