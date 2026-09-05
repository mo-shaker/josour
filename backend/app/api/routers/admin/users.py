import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, status

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import User
from app.schemas.admin import AdminUserCreate, AdminUserOut, AdminUserPatch
from app.services import users as user_service

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
    _: AdminDep,
    db: DbDep,
) -> AdminUserOut:
    """Partial update; unknown fields are rejected rather than ignored.

    `is_active: false` disables the account, `password` sets a new one - both revoke every live
    refresh token, so the user's devices must sign in again - and `unlock: true` clears an active
    lockout and resets the failure counter.
    """
    user = await db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    await user_service.update_user(db, user, payload)
    await db.commit()
    return AdminUserOut.model_validate(user)
