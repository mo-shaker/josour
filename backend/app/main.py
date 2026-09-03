"""FastAPI application factory."""

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.errors import register_exception_handlers
from app.api.routers import api_router
from app.api.routers.health import router as health_router
from app.core.config import Settings, get_settings
from app.core.logging import configure_logging
from app.db.session import dispose_engine

log = logging.getLogger(__name__)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        configure_logging("DEBUG" if settings.is_dev else "INFO")
        log.info("routebridge api starting", extra={"env": settings.env})
        # TODO(week 3): reset presence rows, end dangling sessions, start SessionTimer.
        yield
        await dispose_engine()

    app = FastAPI(
        title="RouteBridge API",
        version="1",
        # /docs only in dev; TODO(week 2): admin-gated docs in production (docs/api.md).
        docs_url="/docs" if settings.is_dev else None,
        redoc_url=None,
        openapi_url="/openapi.json" if settings.is_dev else None,
        lifespan=lifespan,
    )
    register_exception_handlers(app)
    app.include_router(health_router)
    app.include_router(api_router)
    # TODO(week 3): app.include_router(ws_router)  # /ws
    return app


app = create_app()
