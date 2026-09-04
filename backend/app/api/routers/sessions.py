from typing import Annotated

from fastapi import APIRouter, Query

from app.api.deps import AuthDep, DbDep
from app.schemas.sessions import MySessionOut
from app.services import sessions as session_service

router = APIRouter(prefix="/sessions", tags=["sessions"])


@router.get("/me", response_model=list[MySessionOut])
async def my_sessions(
    auth: AuthDep, db: DbDep, limit: Annotated[int, Query(ge=1, le=500)] = 50
) -> list[MySessionOut]:
    return await session_service.list_for_user(db, auth.user.id, limit=limit)
