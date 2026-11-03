from fastapi import APIRouter, Request

from app.api.deps import AuthDep, DbDep, client_ip
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
async def get_hosts(auth: AuthDep, db: DbDep, request: Request) -> list[HostOut]:
    """Same rows, same filter and same order as the `hosts.snapshot` WebSocket frame: the devices
    of the caller's **contacts** that are connected, advertising themselves as available and not
    already in a live session. The caller's own devices are never listed.

    `reachable` is the result of the server's last reachability probe for that device: `true`,
    `false`, or `null` when it has not been probed. `same_public_ip` says the device has the
    caller's own public address, so sites would see the caller's address through it.
    """
    return await list_available_hosts(db, visible_to=auth.user.id, viewer_ip=client_ip(request))
