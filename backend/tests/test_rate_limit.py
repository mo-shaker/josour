"""The four rate limits of ADR-0008, exercised with the limiter *enabled*.

The policy is only as good as the two things these tests pin down: that an attacker cannot spend
someone else's budget, and that ordinary use never meets a limit - in particular the office that
shares one public address, which is what the old per-IP-only limit punished.
"""

from typing import Any

import pytest
from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession
from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

from app.core.rate_limit import TokenBucketLimiter
from app.models import User
from app.models.enums import UserRole
from app.services.auth import MAX_FAILED_LOGINS
from app.services.users import create_user
from tests.conftest import PASSWORD, ActorFactory, AppFactory, LoginBody, make_settings

LOGIN = "/api/v1/auth/login"
REFRESH = "/api/v1/auth/refresh"
PROBE = "/api/v1/probe"


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


def test_token_bucket_refund_returns_one_token_and_never_over_fills() -> None:
    clock = FakeClock()
    limiter = TokenBucketLimiter(2, 60.0, clock=clock)
    assert limiter.check("k").allowed and limiter.check("k").allowed
    assert not limiter.check("k").allowed
    limiter.refund("k")
    assert limiter.check("k").allowed  # exactly one came back
    assert not limiter.check("k").allowed

    for _ in range(5):
        limiter.refund("k")
    assert limiter.check("k").allowed and limiter.check("k").allowed
    assert not limiter.check("k").allowed  # capped at capacity, not 5

    limiter.refund("never-seen")  # a refund for an unknown key does not create a full bucket
    assert "never-seen" not in limiter._buckets


def _limited_app(app_factory: AppFactory, **overrides: Any) -> FastAPI:
    return app_factory(make_settings(rate_limit_enabled=True, **overrides))


def _client(app: FastAPI, ip: str = "127.0.0.1") -> AsyncClient:
    transport = ASGITransport(app=app, client=(ip, 4242))
    return AsyncClient(transport=transport, base_url="http://test")


# ------------------------------------------------------------------ POST /auth/login, per IP


async def test_login_is_limited_per_ip(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    """A distinct email per attempt, so the only thing that can bite is the IP bucket."""
    app = _limited_app(app_factory, login_rate_limit_per_minute=5)
    async with _client(app, "198.51.100.4") as client:
        for i in range(5):
            response = await client.post(LOGIN, json=login_body(email=f"nobody{i}@example.com"))
            assert response.status_code == 401
        blocked = await client.post(LOGIN, json=login_body(email="nobody9@example.com"))
        assert blocked.status_code == 429
        assert blocked.json()["error"]["code"] == "rate_limited"
        assert int(blocked.headers["retry-after"]) >= 1
        # Even an invalid body is answered by the limiter first: a malformed attempt is not free.
        still_blocked = await client.post(LOGIN, json={"email": "x"})
        assert still_blocked.status_code == 429

    async with _client(app, "10.0.0.9") as other_ip:
        assert (await other_ip.post(LOGIN, json=login_body())).status_code == 200


async def test_login_limit_honours_forwarded_ip_behind_trusted_proxy(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    """With uvicorn's proxy-headers middleware (Dockerfile: --proxy-headers) the limiter keys
    by X-Forwarded-For; without it the header is ignored and the peer address is used."""
    app = _limited_app(app_factory, login_rate_limit_per_minute=5)
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


# --------------------------------------------------------------- POST /auth/login, per email


async def test_one_account_cannot_be_hammered_from_many_addresses(
    app_factory: AppFactory, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    """The reservation this closes: twenty addresses used to give twenty times the budget."""
    app = _limited_app(app_factory)
    for attempt in range(5):
        async with _client(app, f"203.0.113.{attempt}") as client:
            response = await client.post(LOGIN, json=login_body(password="wrong"))
            assert response.status_code == 401, attempt

    async with _client(app, "203.0.113.99") as fresh_address:
        blocked = await fresh_address.post(LOGIN, json=login_body(password="wrong"))
    assert blocked.status_code == 429
    assert blocked.json()["error"]["code"] == "rate_limited"
    # 5 attempts per 15 minutes refills one token every three minutes.
    assert int(blocked.headers["retry-after"]) == 180


async def test_the_limit_bites_before_the_account_lockout(
    app_factory: AppFactory, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    """A lockout is a denial of service an attacker can trigger, so the rate limit has to stop
    them first: the email bucket (5) is deliberately smaller than the lockout threshold (10)."""
    app = _limited_app(app_factory)
    statuses = []
    for attempt in range(MAX_FAILED_LOGINS + 2):
        async with _client(app, f"192.0.2.{attempt}") as client:
            statuses.append(
                (await client.post(LOGIN, json=login_body(password="wrong"))).status_code
            )

    assert statuses[:5] == [401] * 5
    assert set(statuses[5:]) == {429}, "the burst must never reach the lockout"
    await db.refresh(user)
    assert user.failed_logins == 5 < MAX_FAILED_LOGINS
    assert user.locked_until is None

    # What the attacker bought is a rolling three-minute throttle on this one account (the
    # bucket refills continuously) rather than a hard fifteen-minute lockout - and the blast
    # radius stops there: every other account still signs in from the same addresses.
    await create_user(db, email="carol@example.com", password=PASSWORD, display_name="Carol")
    await db.commit()
    async with _client(app, "192.0.2.200") as client:
        assert (await client.post(LOGIN, json=login_body())).status_code == 429
        carol = await client.post(LOGIN, json=login_body(email="carol@example.com"))
        assert carol.status_code == 200, carol.text


async def test_an_office_behind_one_nat_is_not_punished_for_a_colleagues_typo(
    app_factory: AppFactory, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    """One public address, two colleagues. The one who cannot type spends only their own budget."""
    await create_user(db, email="bob@example.com", password=PASSWORD, display_name="Bob")
    await db.commit()
    app = _limited_app(app_factory)
    office = "198.51.100.77"

    async with _client(app, office) as client:
        for _ in range(5):
            typo = await client.post(LOGIN, json=login_body(password="wrong"))
            assert typo.status_code == 401
        assert (await client.post(LOGIN, json=login_body(password="wrong"))).status_code == 429

        # Same address, different colleague: unaffected.
        bob = await client.post(LOGIN, json=login_body(email="bob@example.com", name="BOB-PC"))
        assert bob.status_code == 200, bob.text
        # And a whole morning of arrivals from that one address still goes through, which the
        # old five-per-minute-per-IP limit would have refused at the sixth person.
        for _ in range(10):
            again = await client.post(LOGIN, json=login_body(email="bob@example.com"))
            assert again.status_code == 200


async def test_a_successful_login_does_not_spend_the_email_budget(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    """Only failures count, so the limit is invisible to a legitimate user - and the bucket maps
    one-to-one onto the lockout counter it is calibrated against."""
    app = _limited_app(app_factory)
    async with _client(app, "203.0.113.50") as client:
        for _ in range(12):
            assert (await client.post(LOGIN, json=login_body())).status_code == 200
        # The budget is untouched: a full five failures are still available afterwards.
        for _ in range(5):
            assert (await client.post(LOGIN, json=login_body(password="wrong"))).status_code == 401
        assert (await client.post(LOGIN, json=login_body(password="wrong"))).status_code == 429


# ------------------------------------------------------------------------ POST /auth/refresh


async def test_refresh_is_limited_per_ip(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    app = _limited_app(app_factory, refresh_rate_limit_per_minute=3)
    async with _client(app, "203.0.113.60") as client:
        tokens: dict[str, Any] = (await client.post(LOGIN, json=login_body())).json()
        refresh = tokens["refresh_token"]
        for _ in range(3):
            response = await client.post(REFRESH, json={"refresh_token": refresh})
            assert response.status_code == 200
            refresh = response.json()["refresh_token"]

        blocked = await client.post(REFRESH, json={"refresh_token": refresh})
        assert blocked.status_code == 429
        assert blocked.json()["error"]["code"] == "rate_limited"
        assert int(blocked.headers["retry-after"]) >= 1

    async with _client(app, "203.0.113.61") as other_ip:
        assert (await other_ip.post(REFRESH, json={"refresh_token": refresh})).status_code == 200


# -------------------------------------------------------------------------------- POST /probe


@pytest.fixture
def probe_headers() -> dict[str, str]:
    return {}


async def test_probe_is_limited_per_user(app_factory: AppFactory, make_actor: ActorFactory) -> None:
    """Keyed by user, not address: the endpoint dials on the caller's behalf, so changing
    networks must not buy a fresh budget."""
    app = _limited_app(app_factory, probe_rate_limit_per_minute=3)
    scanner = await make_actor("scanner@example.com")
    bystander = await make_actor("bystander@example.com", device_name="OTHER-PC")
    headers = {"Authorization": f"Bearer {scanner.token}"}
    body = {"ip": "10.1.2.3", "port": 443}  # refused target: no socket is opened

    async with _client(app, "203.0.113.70") as client:
        for _ in range(3):
            assert (await client.post(PROBE, json=body, headers=headers)).status_code == 400
        blocked = await client.post(PROBE, json=body, headers=headers)
        assert blocked.status_code == 429
        assert blocked.json()["error"]["code"] == "rate_limited"
        assert int(blocked.headers["retry-after"]) >= 1

    # A different source address does not help the same user...
    async with _client(app, "198.51.100.9") as roaming:
        assert (await roaming.post(PROBE, json=body, headers=headers)).status_code == 429
        # ...but another user has their own budget.
        other = {"Authorization": f"Bearer {bystander.token}"}
        assert (await roaming.post(PROBE, json=body, headers=other)).status_code == 400


async def test_probe_answers_401_before_the_rate_limit(app_factory: AppFactory) -> None:
    app = _limited_app(app_factory, probe_rate_limit_per_minute=1)
    async with _client(app) as client:
        for _ in range(3):
            response = await client.post(PROBE, json={"ip": "8.8.8.8", "port": 443})
            assert response.status_code == 401


# ------------------------------------------------------------------------------- the off switch


async def test_rate_limits_can_be_disabled(
    client: AsyncClient, user: User, login_body: LoginBody, app: FastAPI
) -> None:
    assert app.state.rate_limiters is None
    statuses: list[int] = [
        (await client.post(LOGIN, json=login_body())).status_code for _ in range(8)
    ]
    assert statuses == [200] * 8


async def test_logout_is_not_limited(
    app_factory: AppFactory, user: User, login_body: LoginBody
) -> None:
    """Documented in ADR-0008 as a deliberate omission, so it is pinned here rather than left to
    chance: the path never derives a key and always answers 204."""
    app = _limited_app(app_factory, refresh_rate_limit_per_minute=1)
    async with _client(app, "203.0.113.80") as client:
        for _ in range(8):
            response = await client.post(
                "/api/v1/auth/logout", json={"refresh_token": "not-a-real-token"}
            )
            assert response.status_code == 204


async def test_admin_routes_are_reachable_under_the_limits(
    app_factory: AppFactory, make_actor: ActorFactory
) -> None:
    """Nothing outside the four documented paths gained a limit."""
    app = _limited_app(app_factory, login_rate_limit_per_minute=1, probe_rate_limit_per_minute=1)
    admin = await make_actor("root@example.com", role=UserRole.ADMIN, device_name="ADMIN-PC")
    headers = {"Authorization": f"Bearer {admin.token}"}
    async with _client(app, "203.0.113.90") as client:
        for _ in range(6):
            assert (await client.get("/api/v1/admin/users", headers=headers)).status_code == 200
