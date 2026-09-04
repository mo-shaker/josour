from fastapi import APIRouter, status

from app.api.deps import AuthDep, DbDep
from app.schemas.diagnostics import DiagnosticCreated, DiagnosticIn
from app.services import diagnostics

router = APIRouter(prefix="/diagnostics", tags=["diagnostics"])


@router.post("", status_code=status.HTTP_201_CREATED, response_model=DiagnosticCreated)
async def post_diagnostics(payload: DiagnosticIn, auth: AuthDep, db: DbDep) -> DiagnosticCreated:
    """Stores a ``connect_diagnostics`` row for the caller's device (NAT spike tool, week 2)."""
    row = await diagnostics.store(
        db,
        user_id=auth.user.id,
        device_id=auth.device.id,
        session_id=payload.session_id,
        role=payload.role,
        data=payload.data,
    )
    await db.commit()
    return DiagnosticCreated(id=row.id)
