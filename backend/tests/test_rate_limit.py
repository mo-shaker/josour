from typing import Any

from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

from app.core.rate_limit import TokenBucketLimiter
from app.models import User
from tests.conftest import AppFactory, LoginBody, make_settings

LOGIN = "/api/v1/auth/login"


class FakeClock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


def test_token_bucket_allows_burst_then_refills() -> None:
    clock = FakeClock()
    limiter = TokenBucketLimiter(5, 60.0, clock=clock)
    for _ in range(5):
        assert limiter.check("ip").allowed
    denied = limiter.check("ip")
    assert not denied.allowed and denied.retry_after_seconds == 12
    assert limiter.check("other-ip").allowed  # independent keys

    clock.now += 12
    assert limiter.check("ip").allowed
    assert not limiter.check("ip").allowed

    clock.now += 60
    for _ in range(5):
        assert limiter.check("ip").allowed
    assert not limiter.check("ip").allowed


def test_token_bucket_prunes_idle_keys() -> None:
    clock = FakeClock()
    limiter = TokenBucketLimiter(2, 60.0, clock=clock, max_keys=3)
    for key in ("a", "b", "c"):
        limiter.check(key)
    clock.now += 120  # everyone is refilled -> all idle
    assert limiter.check("d").allowed
    assert set(limiter._buckets) == {"d"}


def _limited_app(app_factory: AppFactory) -> FastAPI:
    return app_factory(make_settings(rate_limit_enabled=True, login_rate_limit_per_minute=5))


async def test_login_is_limited_to_five_per_minute_per_ip(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    app = _limited_app(app_factory)
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as client:
        for _ in range(5):
            response = await client.post(LOGIN, json=login_body(password="wrong"))
            assert response.status_code == 401
        blocked = await client.post(LOGIN, json=login_body())
        assert blocked.status_code == 429
        assert blocked.json()["error"]["code"] == "rate_limited"
        assert int(blocked.headers["retry-after"]) >= 1
        # Even an invalid body is answered by the limiter first.
        still_blocked = await client.post(LOGIN, json={"email": "x"})
        assert still_blocked.status_code == 429

    other_ip = ASGITransport(app=app, client=("10.0.0.9", 4242))
    async with AsyncClient(transport=other_ip, base_url="http://test") as client:
        assert (await client.post(LOGIN, json=login_body())).status_code == 200


async def test_login_limit_honours_forwarded_ip_behind_trusted_proxy(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    """With uvicorn's proxy-headers middleware (Dockerfile: --proxy-headers) the limiter keys
    by X-Forwarded-For; without it the header is ignored and the peer address is used."""
    app = _limited_app(app_factory)
    proxied = ProxyHeadersMiddleware(app, trusted_hosts="*")
    async with AsyncClient(transport=ASGITransport(app=proxied), base_url="http://test") as c:
        for _ in range(5):
            r = await c.post(LOGIN, json=login_body(), headers={"X-Forwarded-For": "203.0.113.7"})
            assert r.status_code == 200
        r = await c.post(LOGIN, json=login_body(), headers={"X-Forwarded-For": "203.0.113.7"})
        assert r.status_code == 429
        r = await c.post(LOGIN, json=login_body(), headers={"X-Forwarded-For": "203.0.113.8"})
        assert r.status_code == 200

    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as c:
        # The direct peer (127.0.0.1) has not been counted yet: the header is not trusted here.
        for _ in range(5):
            r = await c.post(LOGIN, json=login_body(), headers={"X-Forwarded-For": "198.51.100.1"})
            assert r.status_code == 200
        r = await c.post(LOGIN, json=login_body(), headers={"X-Forwarded-For": "198.51.100.2"})
        assert r.status_code == 429


async def test_rate_limit_can_be_disabled(
    client: AsyncClient, user: User, login_body: LoginBody, app: FastAPI
) -> None:
    assert app.state.login_rate_limiter is None
    statuses: list[int] = [
        (await client.post(LOGIN, json=login_body())).status_code for _ in range(8)
    ]
    assert statuses == [200] * 8


async def test_other_auth_routes_are_not_limited(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    app = _limited_app(app_factory)
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as client:
        tokens: dict[str, Any] = (await client.post(LOGIN, json=login_body())).json()
        refresh = tokens["refresh_token"]
        for _ in range(7):
            response = await client.post("/api/v1/auth/refresh", json={"refresh_token": refresh})
            assert response.status_code == 200
            refresh = response.json()["refresh_token"]
