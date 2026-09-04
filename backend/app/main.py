"""FastAPI application factory."""

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.deps import build_login_rate_limiter
from app.api.docs import register_guarded_docs
from app.api.errors import register_exception_handlers
from app.api.routers import api_router
from app.api.routers.health import router as health_router
from app.core.config import Settings, get_settings
from app.core.logging import configure_logging
from app.db.session import dispose_engine
from app.services.events import event_bus
from app.ws.lifecycle import on_shutdown, on_startup
from app.ws.router import router as ws_router
from app.ws.subscribers import register_subscribers

log = logging.getLogger(__name__)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        configure_logging("DEBUG" if settings.is_dev else "INFO")
        log.info("routebridge api starting", extra={"env": settings.env})
        await on_startup()
        try:
            yield
        finally:
            await on_shutdown()
            await dispose_engine()

    app = FastAPI(
        title="RouteBridge API",
        version="1",
        # Open in dev; otherwise served by admin-gated routes (register_guarded_docs).
        docs_url="/docs" if settings.is_dev else None,
        redoc_url=None,
        openapi_url="/openapi.json" if settings.is_dev else None,
        lifespan=lifespan,
    )
    app.state.settings = settings
    app.state.login_rate_limiter = build_login_rate_limiter(settings)
    register_exception_handlers(app)
    if not settings.is_dev:
        register_guarded_docs(app)
    app.include_router(health_router)
    app.include_router(api_router)
    app.include_router(ws_router)  # /ws (docs/ws-protocol.md)
    register_subscribers(event_bus)
    return app


app = create_app()
