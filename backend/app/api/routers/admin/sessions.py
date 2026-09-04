import uuid
from typing import Annotated

from fastapi import APIRouter, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.models.enums import SessionStatus
from app.schemas.sessions import AdminSessionOut
from app.services import sessions as session_service
from app.services.events import event_bus

router = APIRouter(prefix="/sessions")


@router.get("", response_model=list[AdminSessionOut])
async def list_sessions(
    _: AdminDep,
    db: DbDep,
    status: SessionStatus | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminSessionOut]:
    return await session_service.list_all(db, status=status, limit=limit)


@router.post(
    "/{session_id}/terminate", status_code=status.HTTP_204_NO_CONTENT, response_class=Response
)
async def terminate_session(
    session_id: uuid.UUID, auth: AdminDep, db: DbDep, request: Request
) -> Response:
    """Ends a non-ended session with ``admin_terminated`` (404 unknown, 409 already ended) and
    publishes ``SessionEnded`` on the bus, which the WebSocket layer turns into
    ``session.terminate`` for both peers (app.ws.subscribers)."""
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
