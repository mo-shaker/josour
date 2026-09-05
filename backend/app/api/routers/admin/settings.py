from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.schemas.settings import AppSettings, SettingsPatch
from app.services.app_settings import settings_service

router = APIRouter(prefix="/settings")


@router.get(
    "",
    response_model=AppSettings,
    summary="Get the operator settings",
    responses=errors(401, 403),
)
async def get_app_settings(_: AdminDep, db: DbDep) -> AppSettings:
    """The effective settings: the built-in defaults with any stored override applied."""
    return await settings_service.get(db)


@router.patch(
    "",
    response_model=AppSettings,
    summary="Update the operator settings",
    responses=errors(401, 403, 422),
)
async def patch_app_settings(payload: SettingsPatch, _: AdminDep, db: DbDep) -> AppSettings:
    """Partial update; unknown fields are rejected rather than ignored, and the full effective
    settings are returned.

    `log_domains` is echoed to every client in `hello.ack.settings`, so a host always knows in
    advance whether the domain names of a session will be stored. It is `false` by default and
    never causes a domain name to be written to the log in any case.
    """
    return await settings_service.update(db, payload)
