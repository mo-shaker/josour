from httpx import AsyncClient


async def test_healthz_answers_with_the_product_marker(client: AsyncClient) -> None:
    """The client's first-run and settings screens decide "is this a Josour server?" from
    this payload before letting the user past, so the marker is part of the contract
    (``docs/api.md``): without it a wrong address resurfaces later as "wrong password"."""
    from app import __version__

    response = await client.get("/healthz")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "product": "josour", "version": __version__}


async def test_healthz_needs_no_authentication(client: AsyncClient) -> None:
    """The reverse proxy and the deployment smoke test call it before any account exists."""
    assert (await client.get("/healthz", headers={})).status_code == 200


async def test_unknown_route_uses_error_envelope(client: AsyncClient) -> None:
    response = await client.get("/api/v1/does-not-exist")
    assert response.status_code == 404
    assert response.json() == {"error": {"code": "not_found", "message": "Not Found"}}


async def test_a_unique_index_conflict_is_a_409_not_a_500(app_factory) -> None:  # type: ignore[no-untyped-def]
    """Whatever concurrent write a handler does not retry itself still gets the error envelope
    with ``conflict`` - a race is not a server fault."""
    from httpx import ASGITransport
    from sqlalchemy.exc import IntegrityError

    app = app_factory(None)

    @app.get("/boom")
    async def boom() -> None:
        raise IntegrityError("INSERT ...", {}, Exception("UNIQUE constraint failed"))

    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as c:
        response = await c.get("/boom")
    assert response.status_code == 409
    assert response.json()["error"]["code"] == "conflict"
