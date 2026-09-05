from fastapi import APIRouter

from app.api.deps import AuthDep, DbDep
from app.api.openapi import errors
from app.schemas.hosts import HostOut
from app.services.hosts import list_available_hosts

router = APIRouter(prefix="/hosts", tags=["hosts"])


@router.get(
    "",
    response_model=list[HostOut],
    summary="List devices available to host a session",
    responses=errors(401, 403),
)
async def get_hosts(auth: AuthDep, db: DbDep) -> list[HostOut]:
    """Same rows, same filter and same order as the `hosts.snapshot` WebSocket frame: connected,
    advertising themselves as available, not already in a live session, and **excluding the
    caller's own devices**.

    `reachable` is the result of the server's last reachability probe for that device: `true`,
    `false`, or `null` when it has not been probed.
    """
    return await list_available_hosts(db, exclude_user_id=auth.user.id)
