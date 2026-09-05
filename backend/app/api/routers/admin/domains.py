from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.models import AllowlistVersion
from app.schemas.admin import AdminDomainsIn, AdminDomainsOut
from app.services import allowlist
from app.services.events import event_bus

router = APIRouter(prefix="/domains")


def _to_out(version: AllowlistVersion | None) -> AdminDomainsOut:
    if version is None:
        return AdminDomainsOut(version=0, entries=[], updated_at=None)
    return AdminDomainsOut(
        version=version.version, entries=list(version.entries), updated_at=version.created_at
    )


@router.get(
    "",
    response_model=AdminDomainsOut,
    summary="Get the current allow-list version",
    responses=errors(401, 403),
)
async def get_domains(_: AdminDep, db: DbDep) -> AdminDomainsOut:
    """The latest published version with its publication time, or version `0` when nothing has
    been published yet."""
    return _to_out(await allowlist.get_latest_version(db))


@router.put(
    "",
    response_model=AdminDomainsOut,
    summary="Publish a new allow-list version",
    responses=errors(
        401,
        403,
        422,
        custom={
            422: "`validation_error` - one or more entries are invalid; the message lists every "
            "rejected entry and **nothing is published**."
        },
    ),
)
async def put_domains(payload: AdminDomainsIn, auth: AdminDep, db: DbDep) -> AdminDomainsOut:
    """Full replacement, published as a new immutable version and broadcast to every connected
    client as `allowlist.updated`.

    Validation is all-or-nothing: a single invalid entry rejects the whole request, so a
    published version is never partially applied. Literal IP addresses, wildcards, slashes and
    `localhost` (with everything under it) are refused; a leading `=` marks an exact-match entry
    and a trailing `:port` restricts the entry to that port.
    """
    version = await allowlist.replace_entries(db, payload.entries, created_by=auth.user.id)
    await db.commit()
    await event_bus.publish(allowlist.published_event(version))
    return _to_out(version)
