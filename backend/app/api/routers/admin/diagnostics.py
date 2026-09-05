from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.api.openapi import errors
from app.schemas.admin import DiagnosticsSummary
from app.services import diagnostics

router = APIRouter(prefix="/diagnostics")


@router.get(
    "",
    response_model=DiagnosticsSummary,
    summary="Connection success summary",
    responses=errors(401, 403),
)
async def get_diagnostics_summary(_: AdminDep, db: DbDep) -> DiagnosticsSummary:
    """Aggregate that feeds the relay decision gate: how many sessions connected directly, which
    candidate type won, which TLS version was negotiated, and why sessions ended.

    `connect_ok_ratio` counts `connect_result == "ok"` over the sessions that reached a verdict;
    a connect that timed out counts as a failure. All zeros before any session exists.
    """
    return await diagnostics.summarize(db)
