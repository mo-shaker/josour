from typing import Annotated

from fastapi import APIRouter, Header, Query, Response, status
from fastapi.responses import JSONResponse

from app.api.deps import AuthDep, DbDep
from app.api.openapi import errors
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


@router.get(
    "",
    response_model=DomainsOut,
    summary="Get the domain allow-list",
    responses={
        304: {"description": "Not modified - the `If-None-Match` version is still current."},
        **errors(
            401,
            403,
            404,
            422,
            custom={404: "`not_found` - no allow-list version with that number was published."},
        ),
    },
)
async def get_domains(
    _: AuthDep,
    db: DbDep,
    version: Annotated[
        int | None,
        Query(ge=0, description="Fetch one specific published version instead of the latest"),
    ] = None,
    if_none_match: Annotated[str | None, Header()] = None,
) -> Response:
    """The centrally managed allow-list, as an immutable numbered snapshot.

    Carries `ETag: "<version>"`; send it back as `If-None-Match` to get `304` when nothing
    changed. Published versions are never deleted or edited, because a host fetches the exact
    version a pending request will apply (`request.incoming.allowlist_version`) in order to show
    the requester what will really be allowed before accepting.

    Version `0` is the implicit empty baseline before anything has been published.
    """
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
