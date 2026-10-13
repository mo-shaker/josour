"""Contacts over the control channel (ADR-0014): who sees which host, presence, and trust."""

import uuid

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectionRequest, SecurityEvent, Session
from app.models.enums import RequestStatus, SecurityEventType, SessionStatus
from tests.conftest import Actor, ActorFactory, ContactsFactory, WsFactory

CAPS = ["contacts", "offers", "trust_v2"]


def _headers(actor: Actor) -> dict[str, str]:
    return {"Authorization": f"Bearer {actor.token}"}


# ---------------------------------------------------------------- visibility


async def test_a_stranger_never_sees_a_host_on_any_channel(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient
) -> None:
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    friend = await make_actor("friend@example.com", device_name="FRIEND-PC")
    stranger = await make_actor("stranger@example.com", contacts=False, device_name="STRANGER")
    host_ws = await ws_connect(host)
    friend_ws = await ws_connect(friend)
    stranger_ws = await ws_connect(stranger)

    await host_ws.send({"type": "host.available", "available": True})
    update = await friend_ws.expect("hosts.update", skip=frozenset())
    assert [h["device_id"] for h in update["hosts"]] == [host.device_id]
    assert update["hosts"][0]["user_id"] == host.user_id

    # The stranger's list did not change, so it is not even sent one.
    await stranger_ws.send({"type": "ping"})
    assert (await stranger_ws.receive())["type"] == "pong"
    assert (await client.get("/api/v1/hosts", headers=_headers(stranger))).json() == []
    late = await ws_connect(stranger)  # a fresh snapshot is filtered the same way
    assert late.snapshot == {"type": "hosts.snapshot", "hosts": []}

    await late.send(
        {
            "type": "request.create",
            "ref": "s1",
            "host_device_id": host.device_id,
            "duration_min": 30,
        }
    )
    error = await late.expect("error")
    assert (error["code"], error["ref"]) == ("not_contact", "s1")


async def test_becoming_contacts_puts_the_host_in_the_list(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    make_contacts: ContactsFactory,
    client: AsyncClient,
) -> None:
    host = await make_actor("host@example.com", device_name="OFFICE-PC")
    guest = await make_actor("guest@example.com", contacts=False)
    host_ws = await ws_connect(host)
    guest_ws = await ws_connect(guest, capabilities=CAPS)
    await host_ws.send({"type": "host.available", "available": True})
    await host_ws.send({"type": "ping"})
    await host_ws.expect("pong")
    assert (await client.get("/api/v1/hosts", headers=_headers(guest))).json() == []

    # The contact is made over REST, so the change arrives as an event: contacts.changed for
    # the people concerned, then the re-filtered host list.
    await _invite_and_accept(client, inviter=host, invitee=guest)
    assert await guest_ws.expect("contacts.changed", skip=frozenset({"hosts.update"}))
    update = await guest_ws.expect("hosts.update", skip=frozenset({"contacts.changed"}))
    assert [h["device_id"] for h in update["hosts"]] == [host.device_id]


async def _invite_and_accept(client: AsyncClient, *, inviter: Actor, invitee: Actor) -> None:
    sent = await client.post(
        "/api/v1/contacts/invites", json={"email": invitee.user.email}, headers=_headers(inviter)
    )
    assert sent.status_code == 202
    [invite] = (await client.get("/api/v1/contacts/invites", headers=_headers(invitee))).json()[
        "incoming"
    ]
    accepted = await client.post(
        f"/api/v1/contacts/invites/{invite['id']}/accept", headers=_headers(invitee)
    )
    assert accepted.status_code == 200


# ---------------------------------------------------------------- presence and host state


async def test_contacts_presence_full_after_hello_then_deltas(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    alice = await make_actor("alice@example.com")
    bob = await make_actor("bob@example.com")
    await make_actor("carol@example.com")  # a contact who never connects

    alice_ws = await ws_connect(alice, capabilities=CAPS)
    assert alice_ws.presence is not None and alice_ws.presence["full"] is True
    assert {c["online"] for c in alice_ws.presence["contacts"]} == {False}
    assert len(alice_ws.presence["contacts"]) == 2

    bob_ws = await ws_connect(bob, capabilities=CAPS)
    came = await alice_ws.expect("contacts.presence")
    assert came == {
        "type": "contacts.presence",
        "full": False,
        "contacts": [{"user_id": bob.user_id, "online": True}],
    }
    assert {c["user_id"]: c["online"] for c in bob_ws.presence["contacts"]}[alice.user_id] is True

    await bob_ws.disconnect()
    went = await alice_ws.expect("contacts.presence")
    assert went["contacts"] == [{"user_id": bob.user_id, "online": False}]


async def test_presence_is_not_sent_to_a_client_that_did_not_ask(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    alice = await make_actor("alice@example.com")
    bob = await make_actor("bob@example.com")
    alice_ws = await ws_connect(alice)  # an older client: no capabilities
    await ws_connect(bob, capabilities=CAPS)
    frames = await alice_ws.drain(timeout=0.2)
    assert all(f["type"] != "contacts.presence" for f in frames)


async def test_host_state_tells_a_host_its_availability_was_taken(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    host = await make_actor("host@example.com")
    host_ws = await ws_connect(host, capabilities=CAPS)
    await host_ws.send({"type": "host.available", "available": True})
    state = await host_ws.expect("host.state")
    assert state == {"type": "host.state", "available": True, "reachable": None}
    await host_ws.send({"type": "host.available", "available": False})
    assert (await host_ws.expect("host.state"))["available"] is False


# ---------------------------------------------------------------- removal mid-session


async def test_removing_a_contact_ends_the_session_between_them(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient, db: AsyncSession
) -> None:
    guest = await make_actor("guest@example.com", device_name="GUEST-PC")
    host = await make_actor("host@example.com", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest, capabilities=CAPS)
    host_ws = await ws_connect(host, capabilities=CAPS)
    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.expect("hosts.update", skip=frozenset({"contacts.presence"}))
    await guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": host.device_id,
            "duration_min": 30,
        }
    )
    incoming = await host_ws.expect(
        "request.incoming", skip=frozenset({"host.state", "hosts.update", "contacts.presence"})
    )
    await host_ws.send(
        {"type": "request.accept", "ref": "h1", "request_id": incoming["request_id"]}
    )
    session_id = (await guest_ws.expect("request.result", skip=frozenset({"request.created"})))[
        "session_id"
    ]

    removed = await client.delete(f"/api/v1/contacts/{host.user_id}", headers=_headers(guest))
    assert removed.status_code == 204

    noise = frozenset({"session.created", "hosts.update", "contacts.changed", "host.state"})
    for ws in (guest_ws, host_ws):
        terminate = await ws.expect("session.terminate", skip=noise)
        assert terminate == {
            "type": "session.terminate",
            "session_id": session_id,
            "reason": "contact_removed",
        }
    row = await db.get(Session, uuid.UUID(session_id))
    assert row is not None
    assert (row.status, row.end_reason) == (SessionStatus.ENDED, "contact_removed")


async def test_removing_a_contact_withdraws_a_pending_request(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient, db: AsyncSession
) -> None:
    guest = await make_actor("guest@example.com")
    host = await make_actor("host@example.com")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host)
    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.expect("hosts.update", skip=frozenset())
    await guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": host.device_id,
            "duration_min": 30,
        }
    )
    request_id = (await guest_ws.expect("request.created"))["request_id"]
    await host_ws.expect("request.incoming")

    await client.delete(f"/api/v1/contacts/{guest.user_id}", headers=_headers(host))
    result = await guest_ws.expect("request.result")
    assert (result["accepted"], result["reason"]) == (False, "contact_removed")
    assert (await host_ws.expect("request.expired"))["request_id"] == request_id
    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.CANCELLED


# ---------------------------------------------------------------- trust held by the server


async def _trusted_request(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    make_contacts: ContactsFactory,
    *,
    host_trusts_guest: bool,
) -> tuple[Actor, Actor, object, object, dict]:
    guest = await make_actor("guest@example.com", contacts=False)
    host = await make_actor("host@example.com", contacts=False)
    await make_contacts(host.user.id, guest.user.id, a_trusts_b=host_trusts_guest)
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host, capabilities=CAPS)
    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.expect("hosts.update", skip=frozenset())
    await guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": host.device_id,
            "duration_min": 30,
        }
    )
    await guest_ws.expect("request.created")
    incoming = await host_ws.expect(
        "request.incoming", skip=frozenset({"host.state", "hosts.update", "contacts.presence"})
    )
    return guest, host, guest_ws, host_ws, incoming


async def test_request_incoming_says_whether_the_host_trusts_the_guest(
    ws_connect: WsFactory, make_actor: ActorFactory, make_contacts: ContactsFactory
) -> None:
    *_, incoming = await _trusted_request(
        ws_connect, make_actor, make_contacts, host_trusts_guest=True
    )
    assert incoming["trusted"] is True


async def test_a_trust_v2_auto_accept_is_checked_against_the_grant(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    make_contacts: ContactsFactory,
    db: AsyncSession,
) -> None:
    guest, host, guest_ws, host_ws, incoming = await _trusted_request(
        ws_connect, make_actor, make_contacts, host_trusts_guest=True
    )
    await host_ws.send(
        {"type": "request.accept", "ref": "h1", "request_id": incoming["request_id"], "auto": True}
    )
    result = await guest_ws.expect("request.result")
    assert result["accepted"] is True

    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.REQUEST_AUTO_ACCEPTED)
    )
    assert event is not None and event.details["grant"] == "server"
    row = await db.get(ConnectionRequest, uuid.UUID(incoming["request_id"]))
    assert row is not None and row.auto_accepted is True


async def test_a_withdrawn_grant_refuses_the_auto_accept_and_leaves_the_request_open(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    make_contacts: ContactsFactory,
    db: AsyncSession,
) -> None:
    """The host's app saw `trusted` but the grant was withdrawn (from another device) before
    its answer arrived: the server says so instead of letting one more session through."""
    guest, host, guest_ws, host_ws, incoming = await _trusted_request(
        ws_connect, make_actor, make_contacts, host_trusts_guest=True
    )
    await make_contacts(host.user.id, guest.user.id, a_trusts_b=False)

    request_id = incoming["request_id"]
    await host_ws.send(
        {"type": "request.accept", "ref": "h1", "request_id": request_id, "auto": True}
    )
    error = await host_ws.expect("error", skip=frozenset({"hosts.update"}))
    assert (error["code"], error["ref"]) == ("trust_revoked", "h1")
    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.PENDING

    # The host can still say yes by hand.
    await host_ws.send({"type": "request.accept", "ref": "h2", "request_id": request_id})
    assert (await guest_ws.expect("request.result"))["accepted"] is True


async def test_an_outdated_client_is_told_to_upgrade_and_closed(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient, admin_headers: dict
) -> None:
    patched = await client.patch(
        "/api/v1/admin/settings", json={"min_client_version": "2.0"}, headers=admin_headers
    )
    assert patched.status_code == 200
    actor = await make_actor("old@example.com")
    ws = await ws_connect(actor, hello=False)
    await ws.send(
        {
            "type": "hello",
            "token": actor.token,
            "device_id": actor.device_id,
            "app_version": "1.9.3",
        }
    )
    error = await ws.expect("error")
    assert error["code"] == "upgrade_required"
    assert await ws.wait_closed() == 4426

    current = await ws_connect(actor, app_version="2.0.1")
    assert current.hello_ack is not None
    assert "contacts" in current.hello_ack["features"]
