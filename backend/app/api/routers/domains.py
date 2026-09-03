from typing import Annotated

from fastapi import APIRouter, Header, Query, Response, status
from fastapi.responses import JSONResponse

from app.api.deps import AuthDep, DbDep
from app.core.errors import NotFound
from app.schemas.domains import DomainsOut
from app.services import allowlist

router = APIRouter(prefix="/domains", tags=["domains"])


def _etag(version: int) -> str:
    return f'"{version}"'


def _matches(if_none_match: str | None, etag: str) -> bool:
    if not if_none_match:
        return False
    candidates = [c.strip() for c in if_none_match.split(",")]
    return "*" in candidates or any(c.removeprefix("W/") == etag for c in candidates)


@router.get("", response_model=DomainsOut, responses={304: {"description": "Not modified"}})
async def get_domains(
    _: AuthDep,
    db: DbDep,
    version: Annotated[int | None, Query(ge=0, description="Fetch a specific version")] = None,
    if_none_match: Annotated[str | None, Header()] = None,
) -> Response:
    if version is None:
        row = await allowlist.get_latest_version(db)
    elif version == 0:
        row = None  # version 0 is the implicit empty baseline before any publish
    else:
        row = await allowlist.get_version(db, version)
        if row is None:
            raise NotFound(f"Allowlist version {version} not found")

    body = DomainsOut(version=row.version if row else 0, entries=list(row.entries) if row else [])
    etag = _etag(body.version)
    headers = {"ETag": etag, "Cache-Control": "no-cache"}
    if _matches(if_none_match, etag):
        return Response(status_code=status.HTTP_304_NOT_MODIFIED, headers=headers)
    return JSONResponse(content=body.model_dump(), headers=headers)
