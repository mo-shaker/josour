from fastapi import APIRouter, Depends

from app.api.deps import AuthDep, probe_rate_limit
from app.api.openapi import errors
from app.core.errors import ValidationFailed
from app.schemas.probe import ProbeRequest, ProbeResponse
from app.services.reachability_probe import forbidden_target_reason, tcp_probe

router = APIRouter(prefix="/probe", tags=["probe"])


@router.post(
    "",
    response_model=ProbeResponse,
    summary="Check whether a public TCP endpoint accepts connections",
    dependencies=[Depends(probe_rate_limit)],
    responses=errors(
        400,
        401,
        422,
        429,
        custom={
            400: "`validation_error` - the target is loopback, private, link-local, multicast or "
            "otherwise reserved (including IPv4 addresses embedded in 6to4, Teredo and "
            "IPv4-mapped forms).",
            429: "`rate_limited` - the per-user probe budget is spent; retry after "
            "`Retry-After` seconds.",
        },
    ),
)
async def probe(payload: ProbeRequest, _: AuthDep) -> ProbeResponse:
    """Opens a TCP connection to `ip:port` with a 3 second timeout, measures how long the
    handshake took, and closes it immediately. **No bytes are sent**, so this reveals only
    whether the port accepts connections.

    Only public addresses may be named. Because the server dials on the caller's behalf, the
    endpoint is rate limited per user (ADR-0008) so it cannot be used as a slow port scanner
    wearing the server's identity.
    """
    reason = forbidden_target_reason(payload.ip)
    if reason is not None:
        raise ValidationFailed(f"Target address is {reason}", status_code=400)
    result = await tcp_probe(payload.ip, payload.port)
    return ProbeResponse(reachable=result.reachable, latency_ms=result.latency_ms)
