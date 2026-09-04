from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient

from tests.conftest import AppFactory, make_settings


def _client(app: FastAPI) -> AsyncClient:
    return AsyncClient(transport=ASGITransport(app=app), base_url="http://test")


async def test_docs_are_open_in_dev(app_factory: AppFactory) -> None:
    async with _client(app_factory(make_settings(env="dev"))) as client:
        docs = await client.get("/docs")
        assert docs.status_code == 200
        assert "swagger" in docs.text.lower()
        schema = await client.get("/openapi.json")
        assert schema.status_code == 200
        assert "/api/v1/admin/users" in schema.json()["paths"]


async def test_docs_require_admin_bearer_in_prod(
    app_factory: AppFactory, auth_headers: dict, admin_headers: dict
) -> None:
    async with _client(app_factory(make_settings(env="prod"))) as client:
        for path in ("/docs", "/openapi.json"):
            anonymous = await client.get(path)
            assert anonymous.status_code == 401, path
            assert anonymous.json()["error"]["code"] == "unauthorized"

            bogus = await client.get(path, headers={"Authorization": "Bearer nope"})
            assert bogus.status_code == 401, path

            non_admin = await client.get(path, headers=auth_headers)
            assert non_admin.status_code == 403, path
            assert non_admin.json()["error"]["code"] == "forbidden"

        docs = await client.get("/docs", headers=admin_headers)
        assert docs.status_code == 200
        assert "swagger" in docs.text.lower()

        schema = await client.get("/openapi.json", headers=admin_headers)
        assert schema.status_code == 200
        paths = schema.json()["paths"]
        assert "/api/v1/admin/users" in paths and "/api/v1/sessions/me" in paths
        assert "/docs" not in paths and "/openapi.json" not in paths
        assert (await client.get("/redoc", headers=admin_headers)).status_code == 404
