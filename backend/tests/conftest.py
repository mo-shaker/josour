"""Test fixtures.

Uses ``DATABASE_URL_TEST`` (PostgreSQL) when set, otherwise a throw-away SQLite file under
pytest's temp directory. Schema is created with ``metadata.create_all``; every table is emptied
after each test.
"""

import asyncio
import os
import uuid
from collections.abc import AsyncIterator, Awaitable, Callable, Iterator
from contextlib import asynccontextmanager
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Any

import pytest
from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession

os.environ.setdefault("JWT_SECRET", "test-only-secret-not-for-production-use")
os.environ.setdefault("ENV", "test")
# The login limiter is exercised explicitly in tests/test_rate_limit.py.
os.environ.setdefault("RATE_LIMIT_ENABLED", "false")

from app.core.clock import utcnow  # noqa: E402
from app.core.config import Settings, get_settings  # noqa: E402
from app.core.security import AccessClaims, create_access_token  # noqa: E402
from app.db import session as db_session  # noqa: E402
from app.db.session import get_db, make_engine, make_sessionmaker  # noqa: E402
from app.models import Base, ConnectionRequest, Device, Session, User  # noqa: E402
from app.models.enums import RequestStatus, SessionStatus, UserRole  # noqa: E402
from app.services.app_settings import settings_service  # noqa: E402
from app.services.session_timer import scheduler  # noqa: E402
from app.services.users import create_user  # noqa: E402
from app.ws.connection_manager import connection_manager  # noqa: E402
from tests.ws_client import ASGIWebSocket, WebSocketClosed  # noqa: E402

PASSWORD = "correct-horse-battery"
ADMIN_EMAIL = "admin@example.com"


def make_settings(**overrides: Any) -> Settings:
    """Settings built from the test environment with explicit overrides (no .env file)."""
    return Settings(_env_file=None, **overrides)  # type: ignore[call-arg]


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


@pytest.fixture(autouse=True)
def _reset_process_state() -> Iterator[None]:
    """In-process state must not leak between tests (tables are emptied after each).

    The WebSocket registry and the timer scheduler are process-wide singletons, like the
    settings cache, and every test gets its own event loop."""
    settings_service.invalidate()
    connection_manager.reset()
    scheduler.cancel_all()
    yield
    settings_service.invalidate()
    connection_manager.reset()
    scheduler.cancel_all()


@pytest.fixture
async def engine(database_url: str) -> AsyncIterator[AsyncEngine]:
    engine = make_engine(database_url)
    yield engine
    await engine.dispose()


@pytest.fixture
async def db(engine: AsyncEngine) -> AsyncIterator[AsyncSession]:
    async with make_sessionmaker(engine)() as session:
        yield session


AppFactory = Callable[[Settings | None], FastAPI]


@pytest.fixture
def app_factory(engine: AsyncEngine) -> Iterator[AppFactory]:
    """Build an app (optionally with custom ``Settings``) bound to the test database.

    The WebSocket layer, its timers and the lifespan open sessions outside the request cycle
    (``app.db.session.session_scope``), so the module-level factory is pointed at the test
    engine too - not only the ``get_db`` dependency."""
    from app.main import create_app

    sessionmaker = make_sessionmaker(engine)
    previous = (db_session._engine, db_session._sessionmaker)
    db_session._engine, db_session._sessionmaker = engine, sessionmaker

    async def override_get_db() -> AsyncIterator[AsyncSession]:
        async with sessionmaker() as session:
            yield session

    def build(settings: Settings | None = None) -> FastAPI:
        application = create_app(settings)
        application.dependency_overrides[get_db] = override_get_db
        return application

    yield build
    db_session._engine, db_session._sessionmaker = previous


@pytest.fixture
def app(app_factory: AppFactory) -> FastAPI:
    return app_factory(None)


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


@pytest.fixture
async def admin(db: AsyncSession) -> User:
    created = await create_user(
        db, email=ADMIN_EMAIL, password=PASSWORD, display_name="Admin", role=UserRole.ADMIN
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


@pytest.fixture
async def admin_logged_in(
    client: AsyncClient, admin: User, login_body: LoginBody
) -> dict[str, Any]:
    response = await client.post(
        "/api/v1/auth/login", json=login_body(email=ADMIN_EMAIL, name="ADMIN-PC")
    )
    assert response.status_code == 200, response.text
    return response.json()


@pytest.fixture
def admin_headers(admin_logged_in: dict[str, Any]) -> dict[str, str]:
    return {"Authorization": f"Bearer {admin_logged_in['access_token']}"}


DeviceFactory = Callable[[User, str], Awaitable[Device]]


@pytest.fixture
def make_device(db: AsyncSession) -> DeviceFactory:
    async def build(owner: User, name: str) -> Device:
        device = Device(
            user_id=owner.id, name=name, os_version="Windows 11", device_secret_hash="x"
        )
        db.add(device)
        await db.flush()
        return device

    return build


@dataclass(frozen=True, slots=True)
class Actor:
    """A user with one device and a usable access token - one side of a WebSocket test."""

    user: User
    device: Device
    token: str

    @property
    def device_id(self) -> str:
        return str(self.device.id)


ActorFactory = Callable[..., Awaitable[Actor]]


@pytest.fixture
def access_token() -> Callable[[User, Device], str]:
    def build(user: User, device: Device) -> str:
        token, _ = create_access_token(
            make_settings(),
            AccessClaims(user_id=user.id, device_id=device.id, role=user.role),
        )
        return token

    return build


@pytest.fixture
def make_actor(db: AsyncSession, access_token: Callable[[User, Device], str]) -> ActorFactory:
    """Create (and commit) a user + device pair the WebSocket layer can authenticate."""

    async def build(
        email: str = "guest@example.com",
        *,
        display_name: str = "Guest",
        device_name: str = "GUEST-PC",
        role: UserRole = UserRole.USER,
    ) -> Actor:
        user = await create_user(
            db, email=email, password=PASSWORD, display_name=display_name, role=role
        )
        device = Device(
            user_id=user.id, name=device_name, os_version="Windows 11", device_secret_hash="x"
        )
        db.add(device)
        await db.commit()
        return Actor(user=user, device=device, token=access_token(user, device))

    return build


WsFactory = Callable[..., Awaitable[ASGIWebSocket]]


@pytest.fixture
async def ws_connect(app: FastAPI) -> AsyncIterator[WsFactory]:
    """Open a ``/ws`` connection; by default it also performs the ``hello`` handshake and
    stores ``hello_ack`` / ``snapshot`` on the returned client."""
    opened: list[ASGIWebSocket] = []

    async def build(
        actor: Actor | None = None,
        *,
        token: str | None = None,
        device_id: str | None = None,
        hello: bool = True,
        app_version: str = "0.1.0-test",
        diagnostics: dict[str, Any] | None = None,
        headers: dict[str, str] | None = None,
        client: tuple[str, int] = ("203.0.113.10", 51000),
    ) -> ASGIWebSocket:
        ws = ASGIWebSocket(app, headers=headers, client=client)
        opened.append(ws)
        await ws.open()
        if hello:
            payload: dict[str, Any] = {
                "type": "hello",
                "token": token if token is not None else (actor.token if actor else ""),
                "device_id": device_id
                if device_id is not None
                else (actor.device_id if actor else str(uuid.uuid4())),
                "app_version": app_version,
            }
            if diagnostics is not None:
                payload["diagnostics"] = diagnostics
            await ws.send(payload)
            try:
                ws.hello_ack = await ws.expect("hello.ack", skip=frozenset({"error"}))
                ws.snapshot = await ws.expect("hosts.snapshot")
            except WebSocketClosed:
                # A rejected hello: the caller asserts on ``wait_closed()``.
                pass
        return ws

    yield build
    for ws in opened:
        await ws.disconnect(raise_app_error=False)


@pytest.fixture
def run_lifespan(app: FastAPI) -> Callable[[], Any]:
    """Run the application lifespan (startup reset / shutdown) around a block of test code."""

    @asynccontextmanager
    async def runner() -> AsyncIterator[FastAPI]:
        async with app.router.lifespan_context(app):
            yield app

    return runner


SessionFactory = Callable[..., Awaitable[Session]]


@pytest.fixture
def make_session(db: AsyncSession) -> SessionFactory:
    """Insert an accepted connection request plus its session (committed)."""

    async def build(
        guest: User,
        guest_device: Device,
        host: User,
        host_device: Device,
        *,
        status: SessionStatus = SessionStatus.ACTIVE,
        created_at: datetime | None = None,
        **fields: Any,
    ) -> Session:
        request = ConnectionRequest(
            guest_user_id=guest.id,
            guest_device_id=guest_device.id,
            host_user_id=host.id,
            host_device_id=host_device.id,
            requested_minutes=30,
            status=RequestStatus.ACCEPTED,
            expires_at=utcnow() + timedelta(seconds=60),
        )
        db.add(request)
        await db.flush()
        session = Session(
            request_id=request.id,
            guest_user_id=guest.id,
            guest_device_id=guest_device.id,
            host_user_id=host.id,
            host_device_id=host_device.id,
            status=status,
            created_at=created_at or utcnow(),
            expires_at=utcnow() + timedelta(minutes=30),
            **fields,
        )
        db.add(session)
        await db.commit()
        return session

    return build
