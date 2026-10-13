"""Account lifecycle from the admin API: delete, role, email, contacts (ADR-0016/0017)."""

import asyncio
import uuid

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession
from typer.testing import CliRunner

from app.cli import cli
from app.core.security import verify_password_async
from app.models import Contact, Device, SecurityEvent, Session, User
from app.models.enums import DeviceStatus, SecurityEventType, SessionStatus
from tests.conftest import PASSWORD, ActorFactory, ContactsFactory, SessionFactory, WsFactory

USERS = "/api/v1/admin/users"


async def test_delete_anonymises_the_person_and_keeps_the_other_sides_history(
    client: AsyncClient,
    admin_headers: dict,
    make_actor: ActorFactory,
    make_session: SessionFactory,
    ws_connect: WsFactory,
    db: AsyncSession,
) -> None:
    guest = await make_actor("guest@example.com", display_name="Guest")
    host = await make_actor("host@example.com", display_name="Hosty")
    session = await make_session(guest.user, guest.device, host.user, host.device)
    # Captured now: ``expire_all`` below would make reading them a lazy load.
    guest_id, guest_device_id, session_id = guest.user.id, guest.device.id, session.id
    host_ws = await ws_connect(host)
    guest_ws = await ws_connect(guest)

    response = await client.delete(f"{USERS}/{guest_id}", headers=admin_headers)
    assert response.status_code == 204, response.text

    # Both sides hear the session end, and the deleted person's channel is closed.
    terminate = await host_ws.expect("session.terminate")
    assert (terminate["session_id"], terminate["reason"]) == (str(session_id), "admin_terminated")
    assert await guest_ws.wait_closed() == 4403

    db.expire_all()
    user = await db.get(User, guest_id)
    assert user is not None
    assert user.state == "deleted" and user.is_active is False
    assert user.email == f"deleted-{user.id}@deleted.invalid"
    assert user.display_name == "Deleted user"
    assert not await verify_password_async(user.password_hash, PASSWORD)
    device = await db.get(Device, guest_device_id)
    assert device is not None and device.status == DeviceStatus.REVOKED
    assert await db.scalar(select(Contact)) is None

    # The host's own history still has the session, with an end.
    row = await db.get(Session, session_id)
    assert row is not None and row.status == SessionStatus.ENDED
    listed = await client.get(f"{USERS}", params={"state": "deleted"}, headers=admin_headers)
    assert [u["id"] for u in listed.json()] == [str(guest_id)]
    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.USER_DELETED)
    )
    assert event is not None and event.user_id == guest_id

    again = await client.delete(f"{USERS}/{guest_id}", headers=admin_headers)
    assert again.status_code == 409
    patch = await client.patch(
        f"{USERS}/{guest_id}", json={"display_name": "Back"}, headers=admin_headers
    )
    assert patch.status_code == 409


async def test_an_admin_cannot_remove_themselves_or_the_last_admin(
    client: AsyncClient, admin_headers: dict, admin_logged_in: dict
) -> None:
    me = admin_logged_in["user"]["id"]
    for method, path, body in (
        ("DELETE", f"{USERS}/{me}", None),
        ("PATCH", f"{USERS}/{me}", {"is_active": False}),
        ("PATCH", f"{USERS}/{me}", {"role": "user"}),
    ):
        response = await client.request(method, path, json=body, headers=admin_headers)
        assert response.status_code == 409, (method, body, response.text)


async def test_role_and_email_can_be_changed(
    client: AsyncClient, admin_headers: dict, user: User, db: AsyncSession
) -> None:
    promoted = await client.patch(
        f"{USERS}/{user.id}",
        json={"role": "admin", "email": "Alice2@Example.com"},
        headers=admin_headers,
    )
    assert promoted.status_code == 200, promoted.text
    assert (promoted.json()["role"], promoted.json()["email"]) == ("admin", "alice2@example.com")

    taken = await client.patch(
        f"{USERS}/{user.id}", json={"email": "admin@example.com"}, headers=admin_headers
    )
    assert taken.status_code == 409
    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.USER_UPDATED)
    )
    assert event is not None and event.details["role"] == "admin"


async def test_admin_sees_and_removes_contacts_and_can_only_withdraw_trust(
    client: AsyncClient,
    admin_headers: dict,
    make_actor: ActorFactory,
    make_contacts: ContactsFactory,
    db: AsyncSession,
) -> None:
    alice = await make_actor("alice@example.com", display_name="Alice", contacts=False)
    bob = await make_actor("bob@example.com", display_name="Bob", contacts=False)
    alice_id, bob_id = alice.user.id, bob.user.id
    await make_contacts(alice_id, bob_id, a_trusts_b=True)

    listed = (await client.get(f"{USERS}/{alice.user_id}/contacts", headers=admin_headers)).json()
    assert len(listed) == 1
    assert (listed[0]["contact_email"], listed[0]["auto_accept"]) == ("bob@example.com", True)
    assert listed[0]["trusted_by_contact"] is False

    revoke = await client.post(
        f"/api/v1/admin/contacts/{alice.user_id}/{bob.user_id}/revoke-trust",
        headers=admin_headers,
    )
    assert revoke.status_code == 204
    db.expire_all()
    link = await db.get(Contact, (alice_id, bob_id))
    assert link is not None and link.auto_accept is False

    removed = await client.delete(
        f"/api/v1/admin/contacts/{bob_id}/{alice_id}", headers=admin_headers
    )
    assert removed.status_code == 204
    assert await db.scalar(select(Contact)) is None
    missing = await client.delete(
        f"/api/v1/admin/contacts/{bob_id}/{uuid.uuid4()}", headers=admin_headers
    )
    assert missing.status_code == 404


async def test_admin_lists_invitations(
    client: AsyncClient, admin_headers: dict, auth_headers: dict
) -> None:
    await client.post(
        "/api/v1/contacts/invites", json={"email": "zoe@example.com"}, headers=auth_headers
    )
    rows = (await client.get("/api/v1/admin/invites", headers=admin_headers)).json()
    assert [(r["inviter_email"], r["invitee_email"], r["status"]) for r in rows] == [
        ("alice@example.com", "zoe@example.com", "pending")
    ]


runner = CliRunner()


async def test_cli_approves_and_seeds_contacts(
    client: AsyncClient, db: AsyncSession, user: User, admin: User
) -> None:
    await client.post(
        "/api/v1/auth/register",
        json={"email": "pending@example.com", "password": PASSWORD, "display_name": "P"},
    )
    listed = await asyncio.to_thread(runner.invoke, cli, ["list-pending"])
    assert listed.exit_code == 0 and "pending@example.com" in listed.output
    approved = await asyncio.to_thread(runner.invoke, cli, ["approve-user", "pending@example.com"])
    assert approved.exit_code == 0, approved.output

    refused = await asyncio.to_thread(runner.invoke, cli, ["seed-contacts"])
    assert refused.exit_code == 2
    seeded = await asyncio.to_thread(runner.invoke, cli, ["seed-contacts", "--all-active"])
    assert seeded.exit_code == 0, seeded.output
    assert "linked 3 new pair(s)" in seeded.output
    twice = await asyncio.to_thread(runner.invoke, cli, ["seed-contacts", "--all-active"])
    assert "linked 0 new pair(s)" in twice.output

    db.expire_all()
    assert len(list(await db.scalars(select(Contact)))) == 6
