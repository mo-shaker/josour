"""Operator-tunable settings persisted in ``app_settings`` (docs/api.md defaults).

``SettingsService.get()`` is the single read path for these values. Week 2 uses it for
``GET/PATCH /admin/settings``; the WebSocket layer reads it for the ``hello.ack`` ``settings``
object and for the request/connect/session timers. The result is cached in-process and the cache
is dropped by ``update()``; with a single uvicorn worker that is sufficient.
"""

import logging
from typing import Any

from pydantic import ValidationError
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import AppSetting
from app.models.app_setting import DEFAULT_SETTINGS
from app.schemas.settings import AppSettings, SettingsPatch

log = logging.getLogger(__name__)


class SettingsService:
    def __init__(self) -> None:
        self._cached: AppSettings | None = None

    async def get(self, db: AsyncSession) -> AppSettings:
        """Effective settings (defaults overridden by stored rows), cached after the first read."""
        if self._cached is None:
            self._cached = await self._load(db)
        return self._cached

    async def update(self, db: AsyncSession, patch: SettingsPatch) -> AppSettings:
        """Upsert the keys present in ``patch``. Commits and invalidates the cache."""
        changes = patch.model_dump(exclude_unset=True, exclude_none=True)
        current = await self.get(db)
        merged = AppSettings.model_validate({**current.model_dump(), **changes})
        for key, value in changes.items():
            row = await db.get(AppSetting, key)
            if row is None:
                db.add(AppSetting(key=key, value=value))
            else:
                row.value = value
        await db.commit()
        self.invalidate()
        self._cached = merged
        return merged

    def invalidate(self) -> None:
        """Forget the cached values; the next ``get()`` reads the database again."""
        self._cached = None

    async def _load(self, db: AsyncSession) -> AppSettings:
        rows = await db.execute(select(AppSetting.key, AppSetting.value))
        values: dict[str, Any] = dict(DEFAULT_SETTINGS)
        for key, value in rows:
            if key not in DEFAULT_SETTINGS:
                continue
            try:
                AppSettings.model_validate({**values, key: value})
            except ValidationError:
                log.warning("ignoring invalid app_settings row", extra={"key": key})
                continue
            values[key] = value
        return AppSettings.model_validate(values)


settings_service = SettingsService()
"""Process-wide instance shared by the admin router and the WebSocket layer."""
