"""All REST routers mounted under /api/v1 (docs/api.md)."""

from fastapi import APIRouter

from app.api.routers import admin, auth, diagnostics, domains, hosts, me, probe, sessions

api_router = APIRouter(prefix="/api/v1")
api_router.include_router(auth.router)
api_router.include_router(me.router)
api_router.include_router(hosts.router)
api_router.include_router(sessions.router)
api_router.include_router(domains.router)
api_router.include_router(probe.router)
api_router.include_router(diagnostics.router)
api_router.include_router(admin.router)
