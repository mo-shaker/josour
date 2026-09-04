import uuid
from typing import Annotated

from fastapi import APIRouter, Query, status

from app.api.deps import AdminDep, DbDep
from app.core.errors import NotFound
from app.models import User
from app.schemas.admin import AdminUserCreate, AdminUserOut, AdminUserPatch
from app.services import users as user_service

router = APIRouter(prefix="/users")


@router.post("", status_code=status.HTTP_201_CREATED, response_model=AdminUserOut)
async def create_user(payload: AdminUserCreate, _: AdminDep, db: DbDep) -> AdminUserOut:
    user = await user_service.create_user(
        db,
        email=payload.email,
        password=payload.password,
        display_name=payload.display_name,
        role=payload.role,
    )
    await db.commit()
    return AdminUserOut.model_validate(user)


@router.get("", response_model=list[AdminUserOut])
async def list_users(
    _: AdminDep,
    db: DbDep,
    q: Annotated[str | None, Query(max_length=320, description="email/display_name")] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminUserOut]:
    rows = await user_service.list_users(db, q=q, limit=limit)
    return [AdminUserOut.model_validate(u) for u in rows]


@router.patch("/{user_id}", response_model=AdminUserOut)
async def patch_user(
    user_id: uuid.UUID, payload: AdminUserPatch, _: AdminDep, db: DbDep
) -> AdminUserOut:
    user = await db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    await user_service.update_user(db, user, payload)
    await db.commit()
    return AdminUserOut.model_validate(user)
