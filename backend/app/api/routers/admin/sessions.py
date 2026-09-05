import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.api.openapi import errors
from app.models.enums import SessionStatus
from app.schemas.sessions import AdminSessionOut
from app.services import sessions as session_service
from app.services.events import event_bus

router = APIRouter(prefix="/sessions")


@router.get(
    "",
    response_model=list[AdminSessionOut],
    summary="List sessions",
    responses=errors(401, 403, 422),
)
async def list_sessions(
    _: AdminDep,
    db: DbDep,
    status: Annotated[SessionStatus | None, Query(description="Filter by lifecycle state")] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminSessionOut]:
    """Every session, newest first, with both peers named and the connect diagnostics columns
    (`connect_result`, `winner_type`, `tls_version`, `connect_ms`) that feed the relay decision
    gate. Administrative data only - no browsing content is recorded."""
    return await session_service.list_all(db, status=status, limit=limit)


@router.post(
    "/{session_id}/terminate",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Terminate a session",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={
            404: "`not_found` - no session with that id.",
            409: "`conflict` - the session has already ended.",
        },
    ),
)
async def terminate_session(
    session_id: Annotated[uuid.UUID, Path(description="A session that has not ended yet")],
    auth: AdminDep,
    db: DbDep,
    request: Request,
) -> Response:
    """Ends a live session centrally with `end_reason = admin_terminated`.

    Takes the single termination path, so the session key is deleted, both deadlines are
    cancelled, an audit row is written, and `session.terminate` is pushed to both peers over the
    WebSocket. The equivalent from the shell is `manage.py end-session`, which runs outside the
    API process and therefore cannot push the frames.
    """
    event = await session_service.admin_terminate(
        db,
        session_id,
        admin_user_id=auth.user.id,
        admin_device_id=auth.device.id,
        ip=client_ip(request),
    )
    await db.commit()
    await event_bus.publish(event)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
