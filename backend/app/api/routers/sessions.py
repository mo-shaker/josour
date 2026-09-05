from typing import Annotated

from fastapi import APIRouter, Query

from app.api.deps import AuthDep, DbDep
from app.api.openapi import errors
from app.schemas.sessions import MySessionOut
from app.services import sessions as session_service

router = APIRouter(prefix="/sessions", tags=["sessions"])


@router.get(
    "/me",
    response_model=list[MySessionOut],
    summary="List my sessions",
    responses=errors(401, 403, 422),
)
async def my_sessions(
    auth: AuthDep,
    db: DbDep,
    limit: Annotated[int, Query(ge=1, le=500, description="Newest first")] = 50,
) -> list[MySessionOut]:
    """The caller's own sessions, newest first, from either side: `role` says whether they were
    the guest or the host, and the `peer_*` fields name the other party.

    Administrative data only - who, when, how much traffic and why it ended. **No browsing
    content is stored**, and domain names only when the operator has enabled `log_domains`.
    """
    return await session_service.list_for_user(db, auth.user.id, limit=limit)
