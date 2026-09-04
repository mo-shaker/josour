from typing import Annotated

from fastapi import APIRouter, Query

from app.api.deps import AdminDep, DbDep
from app.schemas.admin import SecurityEventOut
from app.services.sessions import list_security_events

router = APIRouter(prefix="/security-events")


@router.get("", response_model=list[SecurityEventOut])
async def get_security_events(
    _: AdminDep,
    db: DbDep,
    type: Annotated[str | None, Query(max_length=64)] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[SecurityEventOut]:
    rows = await list_security_events(db, event_type=type, limit=limit)
    return [SecurityEventOut.model_validate(e) for e in rows]
