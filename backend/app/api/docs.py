"""Admin-gated Swagger UI and OpenAPI schema for non-dev environments (docs/api.md: ``/docs``
is restricted to administrators in production). In ``ENV=dev`` FastAPI serves them openly."""

from fastapi import APIRouter, FastAPI
from fastapi.openapi.docs import get_swagger_ui_html
from fastapi.responses import HTMLResponse, JSONResponse

from app.api.deps import AdminDep

OPENAPI_PATH = "/openapi.json"
DOCS_PATH = "/docs"


def register_guarded_docs(app: FastAPI) -> None:
    """Mount ``/docs`` and ``/openapi.json`` behind ``require_admin`` (401 without a bearer
    token, 403 for non-admins). The app must have been created with ``docs_url=None`` and
    ``openapi_url=None`` so these are the only routes on those paths."""
    router = APIRouter(include_in_schema=False)

    @router.get(OPENAPI_PATH)
    async def openapi_schema(_: AdminDep) -> JSONResponse:
        return JSONResponse(app.openapi())

    @router.get(DOCS_PATH)
    async def swagger_ui(_: AdminDep) -> HTMLResponse:
        return get_swagger_ui_html(openapi_url=OPENAPI_PATH, title=f"{app.title} - Swagger UI")

    app.include_router(router)
