from httpx import AsyncClient


async def test_healthz_answers_with_the_product_marker(client: AsyncClient) -> None:
    """The client's first-run and settings screens decide "is this a RouteBridge server?" from
    this payload before letting the user past, so the marker is part of the contract
    (``docs/api.md``): without it a wrong address resurfaces later as "wrong password"."""
    from app import __version__

    response = await client.get("/healthz")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "product": "routebridge", "version": __version__}


async def test_healthz_needs_no_authentication(client: AsyncClient) -> None:
    """The reverse proxy and the deployment smoke test call it before any account exists."""
    assert (await client.get("/healthz", headers={})).status_code == 200


async def test_unknown_route_uses_error_envelope(client: AsyncClient) -> None:
    response = await client.get("/api/v1/does-not-exist")
    assert response.status_code == 404
    assert response.json() == {"error": {"code": "not_found", "message": "Not Found"}}
