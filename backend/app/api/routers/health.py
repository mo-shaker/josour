from fastapi import APIRouter

from app import __version__

router = APIRouter(tags=["health"])

PRODUCT = "routebridge"
"""Product marker. The client's first-run and settings screens ask this endpoint whether an
address is a RouteBridge server before letting the user past; ``{"status": "ok"}`` alone is
something any proxy or unrelated service can answer, so a wrong address would resurface later as
"wrong password". The marker makes that check mean something. It is not a security control — it
is unauthenticated and trivially forgeable — only a way to fail early and honestly."""


@router.get("/healthz", include_in_schema=False, summary="Liveness probe")
async def healthz() -> dict[str, str]:
    """Unauthenticated liveness check for the reverse proxy, the deployment smoke test and the
    client's "is this a RouteBridge server?" question.

    Kept out of the OpenAPI document on purpose: it is infrastructure, not part of the REST
    contract, but its shape is documented in ``docs/api.md`` because the client depends on it.
    """
    return {"status": "ok", "product": PRODUCT, "version": __version__}
