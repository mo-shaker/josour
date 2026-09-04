from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.schemas.settings import AppSettings, SettingsPatch
from app.services.app_settings import settings_service

router = APIRouter(prefix="/settings")


@router.get("", response_model=AppSettings)
async def get_app_settings(_: AdminDep, db: DbDep) -> AppSettings:
    return await settings_service.get(db)


@router.patch("", response_model=AppSettings)
async def patch_app_settings(payload: SettingsPatch, _: AdminDep, db: DbDep) -> AppSettings:
    return await settings_service.update(db, payload)
