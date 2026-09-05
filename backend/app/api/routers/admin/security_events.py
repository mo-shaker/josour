from typing import Annotated

from fastapi import APIRouter, Query

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.schemas.admin import SecurityEventOut
from app.services.sessions import list_security_events

router = APIRouter(prefix="/security-events")


@router.get(
    "",
    response_model=list[SecurityEventOut],
    summary="Read the security audit trail",
    responses=errors(401, 403, 422),
)
async def get_security_events(
    _: AdminDep,
    db: DbDep,
    type: Annotated[
        str | None, Query(max_length=64, description="Exact event type to filter by")
    ] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[SecurityEventOut]:
    """Security-relevant events, newest first.

    Types: `login_success`, `login_failed` (with `details.reason`: `unknown_user`, `bad_password`,
    `device_secret_invalid`, `device_revoked`, `account_disabled`), `login_locked`, `logout`,
    `refresh_reuse`, `device_revoked`, `session_admin_terminated`, and
    `listener_unauthenticated`.

    `details` never contains a secret, a URL or any browsing content. A `device_revoked` row says
    who did it in `details.by` - `admin`, `owner`, or `auto` for an automatic revocation, which
    also carries the `rule`, the number of `matches` and the `window_minutes` that triggered it
    (ADR-0008).
    """
    rows = await list_security_events(db, event_type=type, limit=limit)
    return [SecurityEventOut.model_validate(e) for e in rows]
