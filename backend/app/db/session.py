"""Async engine / session factory and the FastAPI request-scoped session dependency."""

from collections.abc import AsyncIterator, Awaitable, Callable, Iterable
from contextlib import asynccontextmanager

from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)

from app.core.config import get_settings

_engine: AsyncEngine | None = None
_sessionmaker: async_sessionmaker[AsyncSession] | None = None


def make_engine(url: str) -> AsyncEngine:
    kwargs: dict[str, object] = {"pool_pre_ping": True}
    if url.startswith("sqlite"):
        kwargs = {"connect_args": {"check_same_thread": False}}
    return create_async_engine(url, **kwargs)


def make_sessionmaker(engine: AsyncEngine) -> async_sessionmaker[AsyncSession]:
    return async_sessionmaker(engine, expire_on_commit=False, autoflush=False)


def get_engine() -> AsyncEngine:
    global _engine
    if _engine is None:
        _engine = make_engine(get_settings().database_url)
    return _engine


def get_sessionmaker() -> async_sessionmaker[AsyncSession]:
    global _sessionmaker
    if _sessionmaker is None:
        _sessionmaker = make_sessionmaker(get_engine())
    return _sessionmaker


async def dispose_engine() -> None:
    global _engine, _sessionmaker
    if _engine is not None:
        await _engine.dispose()
    _engine = None
    _sessionmaker = None


async def get_db() -> AsyncIterator[AsyncSession]:
    """FastAPI dependency. Handlers commit explicitly; the session rolls back on error."""
    async with get_sessionmaker()() as session:
        yield session


@asynccontextmanager
async def session_scope() -> AsyncIterator[AsyncSession]:
    """A session outside the request cycle: the WebSocket layer, its timers and the lifespan.

    Same factory as :func:`get_db`, usable with ``async with``; callers commit explicitly."""
    async with get_sessionmaker()() as session:
        yield session


async def commit_retrying_once[T](
    db: AsyncSession, action: Callable[[], Awaitable[T]], *, reload: Iterable[object] = ()
) -> T:
    """Run ``action`` and commit. If a unique index refuses it - the same registration, invitation
    or contact written by a concurrent request a moment earlier - roll back, reload ``reload``
    (rollback expires every loaded object, and an async session cannot lazy-load) and run it once
    more: the second run sees the winner's row and takes its "already there" path, so the caller
    gets the documented answer instead of a 500."""
    try:
        result = await action()
        await db.commit()
        return result
    except IntegrityError:
        await db.rollback()
        for instance in reload:
            await db.refresh(instance)
        result = await action()
        await db.commit()
        return result
