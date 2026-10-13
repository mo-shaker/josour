"""The administration dashboard at ``/admin`` (ADR-0016)."""

import logging

from fastapi import FastAPI, Request
from fastapi.responses import RedirectResponse, Response
from fastapi.staticfiles import StaticFiles

from app.core.config import Settings
from app.db.session import session_scope
from app.services import admin_web_sessions
from app.services.session_timer import scheduler
from app.web.security import WebForbidden, WebNotFound, WebRedirect

log = logging.getLogger(__name__)

PURGE_KEY = "admin-web-purge"
PURGE_EVERY_SECONDS = 3600.0

SECURITY_HEADERS = {
    # No script at all: the pages are plain forms. Inline styles are not needed either.
    "Content-Security-Policy": (
        "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; "
        "frame-ancestors 'none'; base-uri 'none'"
    ),
    "Cache-Control": "no-store",
    # Not "no-referrer": that makes a browser send ``Origin: null`` on our own forms.
    "Referrer-Policy": "same-origin",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
}


def mount(app: FastAPI) -> None:
    from app.web.views import render_error, router

    app.include_router(router)
    app.mount(
        "/admin/static",
        StaticFiles(packages=[("app.web", "static")]),
        name="admin-static",
    )

    @app.exception_handler(WebRedirect)
    async def _redirect(_: Request, exc: WebRedirect) -> Response:
        return RedirectResponse(exc.url, status_code=303)

    @app.exception_handler(WebNotFound)
    async def _not_found(request: Request, _: WebNotFound) -> Response:
        return await render_error(request, "error.not_found", 404)

    @app.exception_handler(WebForbidden)
    async def _forbidden(request: Request, _: WebForbidden) -> Response:
        return await render_error(request, "error.forbidden", 403)

    @app.middleware("http")
    async def _headers(request: Request, call_next):  # type: ignore[no-untyped-def]
        response = await call_next(request)
        if request.url.path == "/admin" or request.url.path.startswith("/admin/"):
            for name, value in SECURITY_HEADERS.items():
                response.headers.setdefault(name, value)
        return response


def start_housekeeping(settings: Settings) -> bool:
    """Hourly: forget dashboard sessions that ended long ago."""
    if not settings.admin_web_enabled:
        return False

    async def run() -> None:
        try:
            async with session_scope() as db:
                removed = await admin_web_sessions.purge(db)
                await db.commit()
            if removed:
                log.info("admin web sessions purged", extra={"rows": removed})
        except Exception:
            log.exception("admin web session purge failed")
        start_housekeeping(settings)

    scheduler.schedule_after(PURGE_KEY, PURGE_EVERY_SECONDS, run)
    return True
