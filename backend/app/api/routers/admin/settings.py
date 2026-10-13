from fastapi import APIRouter, Request

from app.api.deps import AdminDep, DbDep, client_ip
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
async def patch_app_settings(
    payload: SettingsPatch, auth: AdminDep, db: DbDep, request: Request
) -> AppSettings:
    """Partial update; unknown fields are rejected rather than ignored, and the full effective
    settings are returned.

    `log_domains` is echoed to every client in `hello.ack.settings`, so a host always knows in
    advance whether the domain names of a session will be stored. It is `false` by default and
    never causes a domain name to be written to the log in any case.

    `registration_mode` decides whether people can create accounts from the app (`closed`,
    `approval` - the default - or `open`); `min_client_version` refuses older apps with
    `upgrade_required`. Every change is written to the audit trail.
    """
    return await settings_service.update(
        db, payload, actor_user_id=auth.user.id, ip=client_ip(request)
    )
