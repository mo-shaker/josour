"""Workspaces (ADR-0018): placement, isolation, invitations, roles and moving between them."""

from collections.abc import Awaitable, Callable
from dataclasses import dataclass

import pytest
from fastapi import FastAPI
from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Contact, Session, User, Workspace
from app.models.enums import SessionEndReason, SessionStatus, WorkspaceMode
from app.schemas.settings import SettingsPatch
from app.services.app_settings import settings_service
from app.services.outbox import Email, MemoryOutbox, Senders
from app.services.users import create_user
from tests.conftest import PASSWORD, DeviceFactory, LoginBody, SessionFactory

WORKSPACE = "/api/v1/workspace"
MEMBERS = "/api/v1/workspace/members"
WS_INVITES = "/api/v1/workspace/invites"
MY_INVITES = "/api/v1/me/workspace-invites"
CONTACT_INVITES = "/api/v1/contacts/invites"
CONTACTS = "/api/v1/contacts"


@dataclass(frozen=True, slots=True)
class Person:
    user: User
    headers: dict[str, str]


PersonFactory = Callable[..., Awaitable[Person]]


@pytest.fixture
def mail(app: FastAPI) -> MemoryOutbox:
    outbox = MemoryOutbox()
    app.state.senders = Senders(mail=outbox, sms=None)
    return outbox


@pytest.fixture
async def multi(db: AsyncSession) -> None:
    await settings_service.update(db, SettingsPatch(workspace_mode=WorkspaceMode.MULTI))


@pytest.fixture
def person(client: AsyncClient, db: AsyncSession, login_body: LoginBody) -> PersonFactory:
    async def build(email: str, name: str) -> Person:
        user = await create_user(db, email=email, password=PASSWORD, display_name=name)
        await db.commit()
        response = await client.post("/api/v1/auth/login", json=login_body(email=email))
        assert response.status_code == 200, response.text
        return Person(user, {"Authorization": f"Bearer {response.json()['access_token']}"})

    return build


async def _fresh(db: AsyncSession, who: Person) -> User:
    await db.refresh(who.user)
    return who.user


async def _invite_to_workspace(
    client: AsyncClient, inviter: Person, email: str, role: str = "member"
) -> None:
    response = await client.post(
        WS_INVITES, json={"email": email, "role": role}, headers=inviter.headers
    )
    assert response.status_code == 202, response.text


async def _join(client: AsyncClient, who: Person) -> int:
    incoming = (await client.get(MY_INVITES, headers=who.headers)).json()
    assert len(incoming) == 1, incoming
    response = await client.post(f"{MY_INVITES}/{incoming[0]['id']}/accept", headers=who.headers)
    return response.status_code


async def _become_contacts(client: AsyncClient, a: Person, b: Person) -> None:
    response = await client.post(CONTACT_INVITES, json={"email": b.user.email}, headers=a.headers)
    assert response.status_code == 202
    incoming = (await client.get(CONTACT_INVITES, headers=b.headers)).json()["incoming"]
    assert len(incoming) == 1, incoming
    accepted = await client.post(f"{CONTACT_INVITES}/{incoming[0]['id']}/accept", headers=b.headers)
    assert accepted.status_code == 200, accepted.text


# ---------------------------------------------------------------- single mode


async def test_single_mode_keeps_everybody_in_one_workspace(
    client: AsyncClient, person: PersonFactory
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    assert alice.user.workspace_id == bob.user.workspace_id
    assert (alice.user.workspace_role, bob.user.workspace_role) == ("owner", "member")

    disabled = await client.get(WORKSPACE, headers=alice.headers)
    assert disabled.status_code == 403
    assert disabled.json()["error"]["code"] == "workspaces_disabled"
    assert (await client.get(MY_INVITES, headers=alice.headers)).json() == []
    # Contacts work across the whole server, as they always did.
    await _become_contacts(client, alice, bob)


async def test_the_workspace_routes_still_ask_for_a_token_first(client: AsyncClient) -> None:
    assert (await client.get(WORKSPACE)).status_code == 401


# ---------------------------------------------------------------- multi mode: isolation


async def test_multi_mode_gives_every_new_account_its_own_workspace(
    client: AsyncClient, person: PersonFactory, multi: None
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    assert alice.user.workspace_id != bob.user.workspace_id

    mine = (await client.get(WORKSPACE, headers=alice.headers)).json()
    assert mine["name"] == "Alice" and mine["role"] == "owner" and mine["member_count"] == 1
    me = (await client.get("/api/v1/me", headers=alice.headers)).json()
    assert me["workspace_mode"] == "multi" and me["workspace"]["id"] == mine["id"]


async def test_contact_invitations_never_cross_a_workspace(
    client: AsyncClient, person: PersonFactory, multi: None, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")

    # Both ways round, and the inviter cannot tell this from an address nobody uses.
    unknown = await client.post(
        CONTACT_INVITES, json={"email": "nobody@example.com"}, headers=alice.headers
    )
    other = await client.post(
        CONTACT_INVITES, json={"email": bob.user.email}, headers=alice.headers
    )
    assert (other.status_code, other.content) == (unknown.status_code, unknown.content)
    await client.post(CONTACT_INVITES, json={"email": alice.user.email}, headers=bob.headers)

    for who in (alice, bob):
        assert (await client.get(CONTACT_INVITES, headers=who.headers)).json()["incoming"] == []
        assert (await client.get(CONTACTS, headers=who.headers)).json() == []
        assert (await client.get("/api/v1/hosts", headers=who.headers)).json() == []
    assert list(await db.scalars(select(Contact))) == []


async def test_members_list_only_shows_the_callers_workspace(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await person("eve@example.com", "Eve")
    await _invite_to_workspace(client, alice, bob.user.email)
    assert await _join(client, bob) == 204

    members = (await client.get(MEMBERS, headers=alice.headers)).json()
    assert sorted(m["email"] for m in members) == ["alice@example.com", "bob@example.com"]


# ---------------------------------------------------------------- invitations


async def test_joining_a_workspace_by_invitation(
    client: AsyncClient,
    person: PersonFactory,
    multi: None,
    mail: MemoryOutbox,
    db: AsyncSession,
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    bobs_own = bob.user.workspace_id

    await _invite_to_workspace(client, alice, bob.user.email)
    sent = mail.last_to(bob.user.email)
    assert isinstance(sent, Email) and "Alice" in sent.body
    listed = (await client.get(WS_INVITES, headers=alice.headers)).json()
    assert [(i["email"], i["status"]) for i in listed] == [(bob.user.email, "pending")]
    incoming = (await client.get(MY_INVITES, headers=bob.headers)).json()
    assert incoming[0]["workspace_name"] == "Alice" and incoming[0]["from_display_name"] == "Alice"

    assert await _join(client, bob) == 204
    bob_now = await _fresh(db, bob)
    assert (bob_now.workspace_id, bob_now.workspace_role) == (alice.user.workspace_id, "member")
    # The workspace Bob left had nobody else in it, so it is gone.
    assert await db.get(Workspace, bobs_own) is None
    assert (await client.get(WS_INVITES, headers=alice.headers)).json() == []

    # Now they can become contacts.
    await _become_contacts(client, alice, bob)


async def test_an_invitation_answers_the_same_whoever_the_address_is(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    await person("bob@example.com", "Bob")
    answers = set()
    for email in ("alice@example.com", "bob@example.com", "nobody@example.com"):
        response = await client.post(WS_INVITES, json={"email": email}, headers=alice.headers)
        answers.add((response.status_code, response.content))
    assert answers == {(202, b"")}
    # Nothing is sent to somebody who is already a member.
    assert mail.last_to("alice@example.com") is None


async def test_a_declined_invitation_still_looks_pending_to_the_workspace(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite_to_workspace(client, alice, bob.user.email)
    incoming = (await client.get(MY_INVITES, headers=bob.headers)).json()
    declined = await client.post(f"{MY_INVITES}/{incoming[0]['id']}/decline", headers=bob.headers)
    assert declined.status_code == 204
    assert (await client.get(MY_INVITES, headers=bob.headers)).json() == []
    listed = (await client.get(WS_INVITES, headers=alice.headers)).json()
    assert [i["status"] for i in listed] == ["pending"]


async def test_a_withdrawn_invitation_cannot_be_accepted(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite_to_workspace(client, alice, bob.user.email)
    invite_id = (await client.get(WS_INVITES, headers=alice.headers)).json()[0]["id"]
    assert (
        await client.delete(f"{WS_INVITES}/{invite_id}", headers=alice.headers)
    ).status_code == 204
    accepted = await client.post(f"{MY_INVITES}/{invite_id}/accept", headers=bob.headers)
    assert accepted.status_code == 404


# ---------------------------------------------------------------- moving takes contacts along


async def test_leaving_for_another_workspace_removes_contacts_and_ends_the_session(
    client: AsyncClient,
    person: PersonFactory,
    multi: None,
    mail: MemoryOutbox,
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    alice = await person("alice@example.com", "Alice")
    carol = await person("carol@example.com", "Carol")
    dave = await person("dave@example.com", "Dave")
    await _invite_to_workspace(client, alice, carol.user.email)
    assert await _join(client, carol) == 204
    await _become_contacts(client, alice, carol)
    live = await make_session(
        carol.user,
        await make_device(carol.user, "CAROL-PC"),
        alice.user,
        await make_device(alice.user, "ALICE-PC"),
    )
    live_id = live.id

    await _invite_to_workspace(client, dave, carol.user.email)
    assert await _join(client, carol) == 204

    assert (await client.get(CONTACTS, headers=alice.headers)).json() == []
    assert (await client.get(CONTACTS, headers=carol.headers)).json() == []
    db.expire_all()
    ended = await db.get(Session, live_id)
    assert ended is not None and ended.status == SessionStatus.ENDED
    assert ended.end_reason == SessionEndReason.CONTACT_REMOVED


async def test_contact_invitations_sent_before_joining_arrive_after(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    # Alice asks Bob to be a contact before he is in her workspace: it waits, unseen.
    await client.post(CONTACT_INVITES, json={"email": bob.user.email}, headers=alice.headers)
    assert (await client.get(CONTACT_INVITES, headers=bob.headers)).json()["incoming"] == []

    await _invite_to_workspace(client, alice, bob.user.email)
    assert await _join(client, bob) == 204
    incoming = (await client.get(CONTACT_INVITES, headers=bob.headers)).json()["incoming"]
    assert [i["from_email"] for i in incoming] == ["alice@example.com"]


# ---------------------------------------------------------------- roles


async def test_an_owner_hands_over_before_joining_elsewhere(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    dave = await person("dave@example.com", "Dave")
    await _invite_to_workspace(client, alice, bob.user.email)
    assert await _join(client, bob) == 204

    await _invite_to_workspace(client, dave, alice.user.email)
    incoming = (await client.get(MY_INVITES, headers=alice.headers)).json()
    refused = await client.post(f"{MY_INVITES}/{incoming[0]['id']}/accept", headers=alice.headers)
    assert refused.status_code == 409
    assert refused.json()["error"]["code"] == "owner_must_transfer"
    leaving = await client.post(f"{WORKSPACE}/leave", headers=alice.headers)
    assert leaving.json()["error"]["code"] == "owner_must_transfer"

    handed = await client.patch(
        f"{MEMBERS}/{bob.user.id}", json={"role": "owner"}, headers=alice.headers
    )
    assert handed.status_code == 204
    assert ((await _fresh(db, alice)).workspace_role, (await _fresh(db, bob)).workspace_role) == (
        "admin",
        "owner",
    )
    assert await _join(client, alice) == 204


async def test_who_may_invite_change_roles_and_remove(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox, db: AsyncSession
) -> None:
    owner = await person("owner@example.com", "Owner")
    admin = await person("admin@example.com", "Admin")
    member = await person("member@example.com", "Member")
    other = await person("other@example.com", "Other")
    await _invite_to_workspace(client, owner, admin.user.email, role="admin")
    assert await _join(client, admin) == 204
    await _invite_to_workspace(client, admin, member.user.email)
    assert await _join(client, member) == 204

    def code(response) -> str:  # type: ignore[no-untyped-def]
        return response.json()["error"]["code"]

    # A member manages nothing; an admin cannot make admins or change roles.
    as_member = await client.post(
        WS_INVITES, json={"email": "x@example.com"}, headers=member.headers
    )
    assert (as_member.status_code, code(as_member)) == (403, "forbidden")
    admin_invite = await client.post(
        WS_INVITES, json={"email": "x@example.com", "role": "admin"}, headers=admin.headers
    )
    assert admin_invite.status_code == 403
    promote = await client.patch(
        f"{MEMBERS}/{member.user.id}", json={"role": "admin"}, headers=admin.headers
    )
    assert promote.status_code == 403

    # Nobody outside the workspace can be touched.
    outsider = await client.delete(f"{MEMBERS}/{other.user.id}", headers=owner.headers)
    assert outsider.status_code == 404
    # An admin cannot remove the owner; nobody can remove themselves this way.
    assert (
        await client.delete(f"{MEMBERS}/{owner.user.id}", headers=admin.headers)
    ).status_code == 403
    assert (
        await client.delete(f"{MEMBERS}/{owner.user.id}", headers=owner.headers)
    ).status_code == 409

    # An admin removes a member: the member keeps the account, in a workspace of their own.
    removed = await client.delete(f"{MEMBERS}/{member.user.id}", headers=admin.headers)
    assert removed.status_code == 204
    gone = await _fresh(db, member)
    assert gone.workspace_id != owner.user.workspace_id and gone.workspace_role == "owner"
    assert (await client.get(WORKSPACE, headers=member.headers)).json()["member_count"] == 1


async def test_a_member_can_leave(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox, db: AsyncSession
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite_to_workspace(client, alice, bob.user.email)
    assert await _join(client, bob) == 204
    assert (await client.post(f"{WORKSPACE}/leave", headers=bob.headers)).status_code == 204
    assert (await _fresh(db, bob)).workspace_id != alice.user.workspace_id
    assert (await client.get(WORKSPACE, headers=alice.headers)).json()["member_count"] == 1


async def test_renaming_needs_a_manager(
    client: AsyncClient, person: PersonFactory, multi: None, mail: MemoryOutbox
) -> None:
    alice = await person("alice@example.com", "Alice")
    bob = await person("bob@example.com", "Bob")
    await _invite_to_workspace(client, alice, bob.user.email)
    assert await _join(client, bob) == 204
    assert (
        await client.patch(WORKSPACE, json={"name": "Nope"}, headers=bob.headers)
    ).status_code == 403
    renamed = await client.patch(WORKSPACE, json={"name": " Acme "}, headers=alice.headers)
    assert renamed.status_code == 200 and renamed.json()["name"] == "Acme"


# ---------------------------------------------------------------- operator


async def test_the_operator_lists_workspaces_and_places_accounts(
    client: AsyncClient, person: PersonFactory, multi: None, admin_headers: dict
) -> None:
    alice = await person("alice@example.com", "Alice")
    listed = (await client.get("/api/v1/admin/workspaces", headers=admin_headers)).json()
    alices = next(w for w in listed if w["id"] == str(alice.user.workspace_id))
    assert alices["member_count"] == 1 and alices["owner_email"] == "alice@example.com"

    created = await client.post(
        "/api/v1/admin/users",
        json={
            "email": "bob@example.com",
            "password": PASSWORD,
            "display_name": "Bob",
            "workspace_id": str(alice.user.workspace_id),
        },
        headers=admin_headers,
    )
    assert created.status_code == 201, created.text
    body = created.json()
    assert body["workspace_id"] == str(alice.user.workspace_id)
    assert body["workspace_role"] == "member"
    # Without a workspace, multi mode gives the account its own.
    solo = await client.post(
        "/api/v1/admin/users",
        json={"email": "carol@example.com", "password": PASSWORD, "display_name": "Carol"},
        headers=admin_headers,
    )
    assert solo.json()["workspace_role"] == "owner"
    assert solo.json()["workspace_id"] != str(alice.user.workspace_id)
