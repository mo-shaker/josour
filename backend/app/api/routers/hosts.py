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
    """Same rows, same filter and same order as the `hosts.snapshot` WebSocket frame: the devices
    of the caller's **contacts** that are connected, advertising themselves as available and not
    already in a live session. The caller's own devices are never listed.

    `reachable` is the result of the server's last reachability probe for that device: `true`,
    `false`, or `null` when it has not been probed.
    """
    return await list_available_hosts(db, visible_to=auth.user.id)
