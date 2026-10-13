"""What a state change leaves to do *after* its transaction commits.

Admin actions, contact changes and account deletion all end the same way: some control channels
must be closed, some sessions were ended and their peers must be told, some users' contact lists
changed, some pending requests or offers were settled. None of that may be observable before the
row that justifies it is durable, so the services collect it in an :class:`Effects` and the
caller - a REST handler, the web dashboard or the CLI - commits and then calls :func:`apply`.
"""

import uuid
from dataclasses import dataclass, field

from app.services.events import ContactsChanged, SessionEnded, event_bus


@dataclass(frozen=True, slots=True)
class SettledRequest:
    """A pending request or offer that was closed without a session, for the frames that tell
    both sides (``request.result`` / ``request.expired`` / ``offer.result`` / ``offer.closed``)."""

    request_id: uuid.UUID
    initiator: str
    guest_user_id: uuid.UUID
    guest_device_id: uuid.UUID | None
    host_device_id: uuid.UUID
    reason: str


@dataclass(slots=True)
class Effects:
    disconnect: list[uuid.UUID] = field(default_factory=list)
    """Devices whose live control channel must be closed with 4403."""
    session_events: list[SessionEnded] = field(default_factory=list)
    """Sessions that were ended; publishing them sends ``session.terminate`` to both peers."""
    contacts_changed: set[uuid.UUID] = field(default_factory=set)
    """Users whose contacts, invitations or trust changed; they are told ``contacts.changed``."""
    settled: list[SettledRequest] = field(default_factory=list)

    def merge(self, other: "Effects") -> "Effects":
        self.disconnect.extend(other.disconnect)
        self.session_events.extend(other.session_events)
        self.contacts_changed |= other.contacts_changed
        self.settled.extend(other.settled)
        return self


async def apply(effects: Effects) -> None:
    """Run the follow-ups, in the order a peer would want to learn them: the session ends before
    the socket closes, so a guest hears ``session.terminate`` rather than a bare disconnect."""
    from app.services import requests as request_service
    from app.ws import notify
    from app.ws.protocol import CloseCode

    for event in effects.session_events:
        await event_bus.publish(event)
    await request_service.announce_settled(effects.settled)
    for device_id in effects.disconnect:
        await notify.close_device(device_id, CloseCode.NOT_ALLOWED)
    if effects.contacts_changed:
        await event_bus.publish(ContactsChanged(user_ids=tuple(sorted(effects.contacts_changed))))
