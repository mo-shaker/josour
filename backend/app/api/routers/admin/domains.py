from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.models import AllowlistVersion
from app.schemas.admin import AdminDomainsIn, AdminDomainsOut
from app.services import allowlist
from app.services.events import event_bus

router = APIRouter(prefix="/domains")


def _to_out(version: AllowlistVersion | None) -> AdminDomainsOut:
    if version is None:
        return AdminDomainsOut(version=0, entries=[], updated_at=None)
    return AdminDomainsOut(
        version=version.version, entries=list(version.entries), updated_at=version.created_at
    )


@router.get("", response_model=AdminDomainsOut)
async def get_domains(_: AdminDep, db: DbDep) -> AdminDomainsOut:
    return _to_out(await allowlist.get_latest_version(db))


@router.put("", response_model=AdminDomainsOut)
async def put_domains(payload: AdminDomainsIn, auth: AdminDep, db: DbDep) -> AdminDomainsOut:
    """Full replacement; any invalid entry rejects the whole request (422 listing them all).
    Publishes ``AllowlistPublished`` on the bus once committed; the WebSocket layer turns that
    into an ``allowlist.updated`` broadcast (app.ws.subscribers)."""
    version = await allowlist.replace_entries(db, payload.entries, created_by=auth.user.id)
    await db.commit()
    await event_bus.publish(allowlist.published_event(version))
    return _to_out(version)
