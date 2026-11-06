from typing import Annotated

from fastapi import APIRouter, Query

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.schemas.workspaces import AdminWorkspaceOut
from app.services import workspaces as workspace_service

router = APIRouter(prefix="/workspaces")


@router.get(
    "",
    response_model=list[AdminWorkspaceOut],
    summary="List workspaces",
    responses=errors(401, 403, 422),
)
async def list_workspaces(
    _: AdminDep,
    db: DbDep,
    q: Annotated[str | None, Query(max_length=100, description="Substring of the name")] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
) -> list[AdminWorkspaceOut]:
    """Every workspace on the server, newest first, with its member count and owner (ADR-0018).
    In `single` mode there is the default one; in `multi` mode, one per company, family or
    person. The members themselves are `GET /admin/users` (each carries `workspace_id`)."""
    return await workspace_service.list_for_admin(db, q=q, limit=limit, offset=offset)
