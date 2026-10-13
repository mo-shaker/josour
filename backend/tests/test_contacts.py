"""Contacts, invitations and trust over REST (ADR-0014)."""

from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import timedelta

import pytest
from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import Contact, ContactInvite, SecurityEvent, User
from app.models.enums import InviteStatus, SecurityEventType
from app.services.users import create_user
from tests.conftest import PASSWORD, LoginBody

CONTACTS = "/api/v1/contacts"
INVITES = "/api/v1/contacts/invites"


@dataclass(frozen=True, slots=True)
class Person:
    user: User
    headers: dict[str, str]


PersonFactory = Callable[..., Awaitable[Person]]


@pytest.fixture
def person(client: AsyncClient, db: AsyncSession, login_body: LoginBody) -> PersonFactory:
    """A signed-in account: the REST side of a contact test."""

    async def build(email: str, name: str) -> Person:
        user = await create_user(db, email=email, password=PASSWORD, display_name=name)
        await db.commit()
        response = await client.post("/api/v1/auth/login", json=login_body(email=email))
        assert response.status_code == 200, response.text
        return Person(user, {"Authorization": f"Bearer {response.json()['access_token']}"})

    return build


async def _invite(client: AsyncClient, who: Person, email: str) -> None:
    response = await client.post(INVITES, json={"email": email}, headers=who.headers)
    assert response.status_code == 202, response.text


async def _incoming(client: AsyncClient, who: Person) -> list[dict]:
    return (await client.get(INVITES, headers=who.headers)).json()["incoming"]


async def _contacts(client: AsyncClient, who: Person) -> list[dict]:
    response = await client.get(CONTACTS, headers=who.headers)
    assert response.status_code == 200, response.text
    return response.json()


async def _befriend(client: AsyncClient, a: Person, b: Person) -> None:
    await _invite(client, a, b.user.email)
    [invite] = await _incoming(client, b)
    accepted = await client.post(f"{INVITES}/{invite['id']}/accept", headers=b.headers)
    assert accepted.status_code == 200, accepted.text


# ---------------------------------------------------------------- invitations


async def test_inviting_an_unknown_and_a_known_address_look_the_same(
    client: AsyncClient, person: PersonFactory
) -> None:
    """The whole point: an invitation must not reveal who has an account."""
    alice = await person("alice@example.com", "Alice")
    await person("bob@example.com", "Bob")

    known = await client.post(INVITES, json={"email": "bob@example.com"}, headers=alice.headers)
    unknown = await client.post(
        INVITES, json={"email": "nobody@example.com"}, headers=alice.headers
    )
    assert (known.status_code, known.content) == (unknown.status_code, unknown.content)

    outgoing = (await client.get(INVITES, headers=alice.headers)).json()["outgoing"]
    assert sorted((o["email"], o["status"]) for o in outgoing) == [
        ("bob@example.com", "pending"),
        ("nobody@example.com", "pending"),
    ]


async def test_accepting_makes_both_sides_contacts(
    client: AsyncClient, person: PersonFactory
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite(client, alice, "BOB@example.com")

    [invite] = await _incoming(client, bob)
    assert (invite["from_email"], invite["from_display_name"]) == ("alice@example.com", "Alice")

    accepted = await client.post(f"{INVITES}/{invite['id']}/accept", headers=bob.headers)
    assert accepted.status_code == 200, accepted.text
    assert accepted.json()["contact"]["user_id"] == str(alice.user.id)

    [as_bob_sees] = await _contacts(client, bob)
    [as_alice_sees] = await _contacts(client, alice)
    assert as_bob_sees["email"] == "alice@example.com"
    assert as_alice_sees["email"] == "bob@example.com"
    assert as_alice_sees["i_trust"] is False and as_alice_sees["trusts_me"] is False
    # Accepted invitations leave the lists.
    assert await _incoming(client, bob) == []
    assert (await client.get(INVITES, headers=alice.headers)).json()["outgoing"] == []


async def test_a_decline_is_silent(client: AsyncClient, person: PersonFactory) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite(client, alice, bob.user.email)
    [invite] = await _incoming(client, bob)

    declined = await client.post(f"{INVITES}/{invite['id']}/decline", headers=bob.headers)
    assert declined.status_code == 204
    assert await _incoming(client, bob) == []
    # Alice cannot tell a decline from an address nobody uses.
    [outgoing] = (await client.get(INVITES, headers=alice.headers)).json()["outgoing"]
    assert outgoing["status"] == "pending"
    assert await _contacts(client, alice) == []


async def test_inviting_someone_who_already_invited_you_connects_you(
    client: AsyncClient, person: PersonFactory
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite(client, alice, bob.user.email)
    await _invite(client, bob, alice.user.email)

    assert [c["email"] for c in await _contacts(client, alice)] == ["bob@example.com"]
    assert [c["email"] for c in await _contacts(client, bob)] == ["alice@example.com"]
    assert await _incoming(client, alice) == [] and await _incoming(client, bob) == []


async def test_you_cannot_invite_yourself(client: AsyncClient, person: PersonFactory) -> None:
    alice = await person("alice@example.com", "Alice")
    response = await client.post(
        INVITES, json={"email": "Alice@Example.com"}, headers=alice.headers
    )
    assert response.status_code == 422


async def test_an_invitation_waits_for_the_address_to_register(
    client: AsyncClient,
    person: PersonFactory,
    admin_headers: dict,
) -> None:
    alice = await person("alice@example.com", "Alice")
    await _invite(client, alice, "carol@example.com")

    registered = await client.post(
        "/api/v1/auth/register",
        json={"email": "carol@example.com", "password": PASSWORD, "display_name": "Carol"},
    )
    assert registered.status_code == 202
    users = (
        await client.get("/api/v1/admin/users", params={"state": "pending"}, headers=admin_headers)
    ).json()
    approved = await client.post(
        f"/api/v1/admin/users/{users[0]['id']}/approve", headers=admin_headers
    )
    assert approved.status_code == 200

    carol_login = await client.post(
        "/api/v1/auth/login",
        json={
            "email": "carol@example.com",
            "password": PASSWORD,
            "device": {"name": "CAROL-PC", "os_version": "Windows 11"},
        },
    )
    carol = {"Authorization": f"Bearer {carol_login.json()['access_token']}"}
    incoming = (await client.get(INVITES, headers=carol)).json()["incoming"]
    assert [i["from_email"] for i in incoming] == ["alice@example.com"]


async def test_expired_invitations_are_not_delivered_and_can_be_sent_again(
    client: AsyncClient, person: PersonFactory, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite(client, alice, bob.user.email)
    row = await db.scalar(select(ContactInvite))
    assert row is not None
    row.expires_at = utcnow() - timedelta(minutes=1)
    await db.commit()

    assert await _incoming(client, bob) == []
    [outgoing] = (await client.get(INVITES, headers=alice.headers)).json()["outgoing"]
    assert outgoing["status"] == "expired"

    await _invite(client, alice, bob.user.email)
    assert len(await _incoming(client, bob)) == 1
    db.expire_all()
    statuses = sorted(r.status for r in await db.scalars(select(ContactInvite)))
    assert statuses == [InviteStatus.EXPIRED, InviteStatus.PENDING]


async def test_withdrawing_an_invitation(client: AsyncClient, person: PersonFactory) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite(client, alice, bob.user.email)
    [outgoing] = (await client.get(INVITES, headers=alice.headers)).json()["outgoing"]

    # Only the inviter may withdraw it.
    assert (
        await client.delete(f"{INVITES}/{outgoing['id']}", headers=bob.headers)
    ).status_code == 404
    assert (
        await client.delete(f"{INVITES}/{outgoing['id']}", headers=alice.headers)
    ).status_code == 204
    assert await _incoming(client, bob) == []
    assert (await client.get(INVITES, headers=alice.headers)).json()["outgoing"] == []


async def test_only_the_invitee_can_accept(client: AsyncClient, person: PersonFactory) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    mallory = await person("mallory@example.com", "Mallory")
    await _invite(client, alice, bob.user.email)
    [invite] = await _incoming(client, bob)
    stolen = await client.post(f"{INVITES}/{invite['id']}/accept", headers=mallory.headers)
    assert stolen.status_code == 404
    assert await _contacts(client, mallory) == []


# ---------------------------------------------------------------- trust and removal


async def test_trust_is_one_sided_and_owned_by_the_connection_owner(
    client: AsyncClient, person: PersonFactory, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _befriend(client, alice, bob)

    granted = await client.put(
        f"{CONTACTS}/{bob.user.id}/trust", json={"auto_accept": True}, headers=alice.headers
    )
    assert granted.status_code == 200, granted.text
    assert granted.json()["i_trust"] is True and granted.json()["trusts_me"] is False

    [alice_seen_by_bob] = await _contacts(client, bob)
    assert alice_seen_by_bob["trusts_me"] is True and alice_seen_by_bob["i_trust"] is False

    revoked = await client.put(
        f"{CONTACTS}/{bob.user.id}/trust", json={"auto_accept": False}, headers=alice.headers
    )
    assert revoked.json()["i_trust"] is False
    events = [
        e.type
        for e in await db.scalars(
            select(SecurityEvent)
            .where(SecurityEvent.user_id == alice.user.id)
            .order_by(SecurityEvent.created_at)
        )
    ]
    assert SecurityEventType.TRUST_GRANTED in events and SecurityEventType.TRUST_REVOKED in events


async def test_trust_needs_a_contact(client: AsyncClient, person: PersonFactory) -> None:
    alice = await person("alice@example.com", "Alice")
    stranger = await person("stranger@example.com", "Stranger")
    response = await client.put(
        f"{CONTACTS}/{stranger.user.id}/trust", json={"auto_accept": True}, headers=alice.headers
    )
    assert response.status_code == 404


async def test_removing_a_contact_removes_it_for_both(
    client: AsyncClient, person: PersonFactory, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _befriend(client, alice, bob)

    assert (
        await client.delete(f"{CONTACTS}/{alice.user.id}", headers=bob.headers)
    ).status_code == 204
    assert await _contacts(client, alice) == [] and await _contacts(client, bob) == []
    assert await db.scalar(select(Contact)) is None
    again = await client.delete(f"{CONTACTS}/{alice.user.id}", headers=bob.headers)
    assert again.status_code == 404


async def test_invitations_are_rate_limited_per_user(
    app_factory, db: AsyncSession, login_body: LoginBody
) -> None:
    from httpx import ASGITransport

    from tests.conftest import make_settings

    app = app_factory(make_settings(rate_limit_enabled=True, invite_rate_limit_per_day=2))
    await create_user(db, email="alice@example.com", password=PASSWORD, display_name="Alice")
    await db.commit()
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as c:
        token = (await c.post("/api/v1/auth/login", json=login_body())).json()["access_token"]
        headers = {"Authorization": f"Bearer {token}"}
        codes = [
            (
                await c.post(INVITES, json={"email": f"x{i}@example.com"}, headers=headers)
            ).status_code
            for i in range(3)
        ]
    assert codes == [202, 202, 429]
