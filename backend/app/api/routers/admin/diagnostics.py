from fastapi import APIRouter

from app.api.deps import AdminDep, DbDep
from app.schemas.admin import DiagnosticsSummary
from app.services import diagnostics

router = APIRouter(prefix="/diagnostics")


@router.get("", response_model=DiagnosticsSummary)
async def get_diagnostics_summary(_: AdminDep, db: DbDep) -> DiagnosticsSummary:
    return await diagnostics.summarize(db)
