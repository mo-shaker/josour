import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import User
from app.models.enums import UserRole
from app.schemas.admin import AdminUserCreate, AdminUserOut, AdminUserPatch
from app.schemas.contacts import AdminContactOut
from app.services import contacts as contact_service
from app.services import effects
from app.services import users as user_service
from app.services.effects import Effects

router = APIRouter(prefix="/users")

UserId = Annotated[uuid.UUID, Path(description="Account id")]


async def _get(db: DbDep, user_id: uuid.UUID) -> User:
    user = await db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    return user


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
async def create_user(
    payload: AdminUserCreate, auth: AdminDep, db: DbDep, request: Request
) -> AdminUserOut:
    """Creates an account. The password is hashed with argon2id (ADR-0007) and never stored or
    logged in the clear. Email addresses are lower-cased and must be unique. An account an
    administrator creates is approved and active at once, and a `user_created` row names who
    created it and with which role."""
    user = await user_service.create_user(
        db,
        email=payload.email,
        password=payload.password,
        display_name=payload.display_name,
        role=payload.role,
    )
    user_service.record_created(db, user, actor_user_id=auth.user.id, ip=client_ip(request))
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
    state: Annotated[
        user_service.UserState | None,
        Query(description="active, disabled, pending (waiting for approval), rejected, deleted"),
    ] = None,
    role: Annotated[UserRole | None, Query(description="admin or user")] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
) -> list[AdminUserOut]:
    """Accounts, newest first, with their lockout state (`failed_logins`, `locked_until`) and
    where they stand (`state`). `state=pending` is the queue of registrations to approve."""
    rows = await user_service.list_users(
        db, q=q, state=state, role=role, limit=limit, offset=offset
    )
    return [AdminUserOut.model_validate(u) for u in rows]


@router.patch(
    "/{user_id}",
    response_model=AdminUserOut,
    summary="Update a user",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={
            404: "`not_found` - no account with that id.",
            409: "`conflict` - the email is taken; the account is deleted; it is still pending "
            "(approve it instead); or the change would leave no active administrator, or "
            "disable or demote yourself.",
        },
    ),
)
async def patch_user(
    user_id: UserId,
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
    user = await _get(db, user_id)
    disconnect = await user_service.update_user(
        db, user, payload, actor_user_id=auth.user.id, ip=client_ip(request)
    )
    await db.commit()
    # After the commit, as with device revocation: the socket must never close before the row
    # that justifies it is durable.
    await effects.apply(Effects(disconnect=disconnect))
    return AdminUserOut.model_validate(user)


@router.post(
    "/{user_id}/approve",
    response_model=AdminUserOut,
    summary="Approve a registration",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={409: "`conflict` - the account is already approved, or deleted."},
    ),
)
async def approve_user(
    user_id: UserId, auth: AdminDep, db: DbDep, request: Request
) -> AdminUserOut:
    """Lets a self-registered account sign in, and delivers any contact invitations that were
    waiting for its address. A rejected account can be approved after all."""
    user = await _get(db, user_id)
    await user_service.approve_user(db, user, actor_user_id=auth.user.id, ip=client_ip(request))
    await db.commit()
    return AdminUserOut.model_validate(user)


@router.post(
    "/{user_id}/reject",
    response_model=AdminUserOut,
    summary="Reject a registration",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={409: "`conflict` - the account is not pending."},
    ),
)
async def reject_user(user_id: UserId, auth: AdminDep, db: DbDep, request: Request) -> AdminUserOut:
    """Turns a pending registration down. The account stays, so the address cannot simply
    register again; it can never sign in unless an administrator approves it later."""
    user = await _get(db, user_id)
    await user_service.reject_user(db, user, actor_user_id=auth.user.id, ip=client_ip(request))
    await db.commit()
    return AdminUserOut.model_validate(user)


@router.delete(
    "/{user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Delete a user",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={
            409: "`conflict` - already deleted, your own account, or the last active administrator."
        },
    ),
)
async def delete_user(user_id: UserId, auth: AdminDep, db: DbDep, request: Request) -> Response:
    """Deletes the person, not the history. Their email, name and password are replaced, every
    device is revoked and disconnected, their contacts and invitations are removed and a live
    session is ended (`admin_terminated`); the row stays so that the people they shared a
    connection with keep their own session history."""
    user = await _get(db, user_id)
    result = await user_service.delete_user(
        db, user, actor_user_id=auth.user.id, ip=client_ip(request)
    )
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get(
    "/{user_id}/contacts",
    response_model=list[AdminContactOut],
    summary="List a user's contacts",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - no account with that id."}),
)
async def list_user_contacts(user_id: UserId, _: AdminDep, db: DbDep) -> list[AdminContactOut]:
    """Who this user can share a connection with, and the trust each side has granted."""
    await _get(db, user_id)
    return await contact_service.list_for_admin(db, user_id)
