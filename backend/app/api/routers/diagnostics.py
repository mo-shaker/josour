from fastapi import APIRouter, Depends, Request, status

from app.api.deps import AuthDep, DbDep, client_ip, diagnostics_rate_limit
from app.api.openapi import errors
from app.schemas.diagnostics import DiagnosticCreated, DiagnosticIn
from app.services import diagnostics

router = APIRouter(prefix="/diagnostics", tags=["diagnostics"])


@router.post(
    "",
    status_code=status.HTTP_201_CREATED,
    response_model=DiagnosticCreated,
    summary="Report connection diagnostics",
    dependencies=[Depends(diagnostics_rate_limit)],
    responses=errors(
        401,
        403,
        404,
        422,
        429,
        custom={
            403: "`forbidden` - the named session exists but the caller is not one of its two "
            "participants.",
            404: "`not_found` - no session with that id.",
            422: "`validation_error` - malformed body, or `data` larger than 64 KB serialised.",
            429: "`rate_limited` - the per-user budget (60 an hour) is spent; retry after "
            "`Retry-After` seconds.",
        },
    ),
)
async def post_diagnostics(
    payload: DiagnosticIn, auth: AuthDep, db: DbDep, request: Request
) -> DiagnosticCreated:
    """Stores a `connect_diagnostics` row for the calling device (taken from the token, never
    from the body). Used by the NAT model tool and by clients reporting a failed connect; the
    aggregate is `GET /admin/diagnostics`.

    `data` is free-form, at most 64 KB serialised, and **must never carry browsing content** -
    no URLs, no headers, no request bodies.

    **Reserved key.** When `data` carries a positive numeric `listener_unauthenticated`, the
    server also writes a `security_events` row of that type, honouring the optional
    `listener_port` and `unauthenticated_peers` (at most 10 IP addresses; anything that is not
    an IP address is dropped). It records a count and source addresses, nothing else.
    """
    row = await diagnostics.store(
        db,
        user_id=auth.user.id,
        device_id=auth.device.id,
        session_id=payload.session_id,
        role=payload.role,
        data=payload.data,
        ip=client_ip(request),
    )
    await db.commit()
    return DiagnosticCreated(id=row.id)
