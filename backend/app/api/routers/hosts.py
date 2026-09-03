from fastapi import APIRouter

from app.api.deps import AuthDep, DbDep
from app.schemas.hosts import HostOut
from app.services.hosts import list_available_hosts

router = APIRouter(prefix="/hosts", tags=["hosts"])


@router.get("", response_model=list[HostOut])
async def get_hosts(_: AuthDep, db: DbDep) -> list[HostOut]:
    return await list_available_hosts(db)
