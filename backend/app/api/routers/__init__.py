"""All REST routers mounted under /api/v1 (docs/api.md)."""

from fastapi import APIRouter

from app.api.routers import auth, domains, hosts, me, probe

api_router = APIRouter(prefix="/api/v1")
api_router.include_router(auth.router)
api_router.include_router(me.router)
api_router.include_router(hosts.router)
api_router.include_router(domains.router)
api_router.include_router(probe.router)
# TODO(week 2): sessions (GET /sessions/me) and admin_* routers.
