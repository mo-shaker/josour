from fastapi import APIRouter

from app.api.deps import AuthDep
from app.core.errors import ValidationFailed
from app.schemas.probe import ProbeRequest, ProbeResponse
from app.services.reachability_probe import forbidden_target_reason, tcp_probe

router = APIRouter(prefix="/probe", tags=["probe"])


@router.post("", response_model=ProbeResponse)
async def probe(payload: ProbeRequest, _: AuthDep) -> ProbeResponse:
    reason = forbidden_target_reason(payload.ip)
    if reason is not None:
        raise ValidationFailed(f"Target address is {reason}", status_code=400)
    result = await tcp_probe(payload.ip, payload.port)
    return ProbeResponse(reachable=result.reachable, latency_ms=result.latency_ms)
