"""Admin REST API under ``/api/v1/admin`` (docs/api.md "الإدارة"); every route requires
``role = admin`` via ``require_admin``."""

from fastapi import APIRouter, Depends

from app.api.deps import require_admin
from app.api.routers.admin import (
    devices,
    diagnostics,
    domains,
    security_events,
    sessions,
    settings,
    users,
)

router = APIRouter(prefix="/admin", tags=["admin"], dependencies=[Depends(require_admin)])
for module in (users, devices, domains, sessions, security_events, diagnostics, settings):
    router.include_router(module.router)
