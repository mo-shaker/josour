import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, Request, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import User
from app.schemas.admin import AdminUserCreate, AdminUserOut, AdminUserPatch
from app.services import users as user_service
from app.ws import notify
from app.ws.protocol import CloseCode

router = APIRouter(prefix="/users")


@router.post(
    "",
    status_code=status.HTTP_201_CREATED,
    response_model=AdminUserOut,
    summary="Create a user",
    responses=errors(
        401,
        403,
        409,
        422,
        custom={409: "`conflict` - that email address already belongs to an account."},
    ),
)
async def create_user(payload: AdminUserCreate, _: AdminDep, db: DbDep) -> AdminUserOut:
    """Creates an account. The password is hashed with argon2id (ADR-0007) and never stored or
    logged in the clear. Email addresses are lower-cased and must be unique."""
    user = await user_service.create_user(
        db,
        email=payload.email,
        password=payload.password,
        display_name=payload.display_name,
        role=payload.role,
    )
    await db.commit()
    return AdminUserOut.model_validate(user)


@router.get(
    "",
    response_model=list[AdminUserOut],
    summary="List users",
    responses=errors(401, 403, 422),
)
async def list_users(
    _: AdminDep,
    db: DbDep,
    q: Annotated[
        str | None, Query(max_length=320, description="Substring of email or display name")
    ] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminUserOut]:
    """Accounts with their lockout state: `failed_logins` and `locked_until` show whether an
    account is currently locked out (10 failures lock it for 15 minutes)."""
    rows = await user_service.list_users(db, q=q, limit=limit)
    return [AdminUserOut.model_validate(u) for u in rows]


@router.patch(
    "/{user_id}",
    response_model=AdminUserOut,
    summary="Update a user",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - no account with that id."}),
)
async def patch_user(
    user_id: Annotated[uuid.UUID, Path(description="Account to update")],
    payload: AdminUserPatch,
    auth: AdminDep,
    db: DbDep,
    request: Request,
) -> AdminUserOut:
    """Partial update; unknown fields are rejected rather than ignored.

    `password` sets a new one and `unlock: true` clears an active lockout and resets the failure
    counter. Both `password` and `is_active: false` revoke every live refresh token, so the user's
    devices must sign in again.

    `is_active: false` also **takes effect immediately**: the user's presence is cleared and every
    live control channel they hold is closed with WebSocket code 4403, the same way revoking a
    single device works. Without that, revoking refresh tokens alone left a disabled account
    browsing through somebody else's connection until its access token expired. A `user_deactivated`
    row names the administrator who did it.

    The devices themselves are not revoked: the account is disabled, not the hardware.
    """
    user = await db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    disconnect = await user_service.update_user(
        db, user, payload, actor_user_id=auth.user.id, ip=client_ip(request)
    )
    await db.commit()
    # After the commit, as with device revocation: the socket must never close before the row
    # that justifies it is durable.
    for device_id in disconnect:
        await notify.close_device(device_id, CloseCode.NOT_ALLOWED)
    return AdminUserOut.model_validate(user)
