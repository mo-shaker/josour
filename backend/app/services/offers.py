"""Offers: the host side starting a session (ADR-0014, docs/ws-protocol.md section 5b).

``request.create`` is the guest asking for a contact's connection; ``offer.create`` is the other
direction - a host offering theirs. Both live in ``connection_requests`` (``initiator`` tells
them apart) and both end in the same session, opened through ``requests.open_session``.

An offer is addressed to the guest **user**: it rings every connected device of theirs that
declared the ``offers`` capability, and whichever accepts first becomes the session's guest
device; the others are told ``offer.closed(accepted_elsewhere)``.

The guest always has to accept. Taking an offer points the guest's own machine at somebody
else's connection, so - unlike a host's standing trust about its *own* address - there is no
"accept offers automatically" switch.
"""

import logging
import uuid
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.config import Settings, get_settings
from app.db.session import session_scope
from app.models import ConnectionRequest, User
from app.models.enums import RequestInitiator, RequestStatus
from app.services import allowlist
from app.services import contacts as contact_service
from app.services import requests as request_service
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.session_timer import scheduler
from app.ws import notify
from app.ws.connection_manager import Connection, connection_manager
from app.ws.protocol import (
    Capability,
    ErrorCode,
    OfferClosed,
    OfferCreated,
    OfferIncoming,
    OfferResult,
    WsError,
)

log = logging.getLogger(__name__)

timer_key = request_service.offer_timer_key


async def _free_guest_devices(db: AsyncSession, guest_user_id: uuid.UUID) -> list[Connection]:
    """The guest's connected devices that can take an offer right now."""
    free = []
    for connection in connection_manager.for_user(guest_user_id):
        if not connection.can(Capability.OFFERS):
            continue
        if await session_service.get_live_for_device(db, connection.device_id) is not None:
            continue
        pending = await db.scalar(
            select(ConnectionRequest.id).where(
                ConnectionRequest.guest_device_id == connection.device_id,
                ConnectionRequest.status == RequestStatus.PENDING,
            )
        )
        if pending is None:
            free.append(connection)
    return free


# ---------------------------------------------------------------- create / cancel


async def create(
    *,
    host_user_id: uuid.UUID,
    host_device_id: uuid.UUID,
    guest_user_id: uuid.UUID,
    duration_min: int,
    ref: str,
) -> ConnectionRequest:
    async with session_scope() as db:
        settings = await settings_service.get(db)
        request_service.validate_duration(duration_min, settings, ref=ref)
        if guest_user_id == host_user_id:
            raise WsError(ErrorCode.BAD_REQUEST, "Cannot offer to yourself", ref=ref)
        guest_user = await db.get(User, guest_user_id)
        if guest_user is None or not await contact_service.are_contacts(
            db, host_user_id, guest_user_id
        ):
            raise WsError(ErrorCode.NOT_CONTACT, "You can only offer to your contacts", ref=ref)
        host = await request_service.load_party(db, host_user_id, host_device_id)

        if await session_service.get_live_for_user(db, host_user_id) is not None:
            raise WsError(ErrorCode.SESSION_EXISTS, "You already have a session", ref=ref)
        if await request_service.device_is_answering(db, host_device_id):
            raise WsError(ErrorCode.REQUEST_PENDING, "This device has a question open", ref=ref)
        if await session_service.get_live_for_user(db, guest_user_id) is not None:
            raise WsError(ErrorCode.GUEST_UNAVAILABLE, "The contact is in a session", ref=ref)
        targets = await _free_guest_devices(db, guest_user_id)
        if not targets:
            raise WsError(
                ErrorCode.GUEST_UNAVAILABLE, "The contact has no device that can accept", ref=ref
            )

        now = utcnow()
        offer = ConnectionRequest(
            guest_user_id=guest_user_id,
            guest_device_id=None,
            host_user_id=host_user_id,
            host_device_id=host_device_id,
            requested_minutes=duration_min,
            status=RequestStatus.PENDING,
            initiator=RequestInitiator.HOST,
            created_at=now,
            expires_at=now + timedelta(seconds=settings.request_timeout_seconds),
        )
        db.add(offer)
        await db.commit()
        expires_at = ensure_utc(offer.expires_at)
        assert expires_at is not None
        allowlist_version = await allowlist.current_version(db)

    await notify.send_to_device(
        host_device_id, OfferCreated(ref=ref, offer_id=offer.id, expires_at=expires_at)
    )
    incoming = OfferIncoming(
        offer_id=offer.id,
        host_user_id=host.user.id,
        host_device_id=host.device.id,
        host_name=host.user.display_name,
        host_device=host.device.name,
        duration_min=duration_min,
        allowlist_version=allowlist_version,
        expires_at=expires_at,
    )
    for connection in targets:
        await connection.send(incoming)
    _schedule_expiry(offer.id, expires_at)
    return offer


async def _load_pending(db: AsyncSession, offer_id: uuid.UUID, *, ref: str) -> ConnectionRequest:
    offer = await db.get(ConnectionRequest, offer_id)
    if offer is None or offer.initiator != RequestInitiator.HOST:
        raise WsError(ErrorCode.NOT_FOUND, "Unknown offer", ref=ref)
    return offer


def _still_pending(offer: ConnectionRequest, *, ref: str) -> None:
    if offer.status != RequestStatus.PENDING:
        raise WsError(ErrorCode.NOT_FOUND, "Offer is no longer pending", ref=ref)


async def cancel(*, offer_id: uuid.UUID, device_id: uuid.UUID, ref: str) -> None:
    """``offer.cancel`` from the offering device."""
    async with session_scope() as db:
        offer = await _load_pending(db, offer_id, ref=ref)
        if offer.host_device_id != device_id:
            raise WsError(ErrorCode.FORBIDDEN, "This offer is not yours", ref=ref)
        _still_pending(offer, ref=ref)
        offer.status = RequestStatus.CANCELLED
        offer.responded_at = utcnow()
        await db.commit()
        guest_user_id = offer.guest_user_id
    scheduler.cancel(timer_key(offer_id))
    await notify.send_to_device(
        device_id, OfferResult(offer_id=offer_id, accepted=False, reason="cancelled")
    )
    await notify.send_to_user(
        guest_user_id,
        OfferClosed(offer_id=offer_id, reason="cancelled"),
        capability=Capability.OFFERS,
    )


# ---------------------------------------------------------------- the guest's answer


async def reject(
    *, offer_id: uuid.UUID, user_id: uuid.UUID, device_id: uuid.UUID, ref: str
) -> None:
    """``offer.reject`` from any of the guest's devices: no for all of them."""
    async with session_scope() as db:
        offer = await _load_pending(db, offer_id, ref=ref)
        if offer.guest_user_id != user_id:
            raise WsError(ErrorCode.FORBIDDEN, "This offer is not addressed to you", ref=ref)
        _still_pending(offer, ref=ref)
        offer.status = RequestStatus.REJECTED
        offer.responded_at = utcnow()
        await db.commit()
        host_device_id = offer.host_device_id
    scheduler.cancel(timer_key(offer_id))
    await notify.send_to_device(
        host_device_id, OfferResult(offer_id=offer_id, accepted=False, reason="rejected")
    )
    await notify.send_to_user(
        user_id, OfferClosed(offer_id=offer_id, reason="rejected"), capability=Capability.OFFERS
    )


async def accept(
    *,
    offer_id: uuid.UUID,
    user_id: uuid.UUID,
    device_id: uuid.UUID,
    ref: str,
    runtime: Settings | None = None,
) -> None:
    """``offer.accept``: the accepting device becomes the guest device and the session opens."""
    runtime = runtime or get_settings()
    async with session_scope() as db:
        settings = await settings_service.get(db)
        offer = await _load_pending(db, offer_id, ref=ref)
        if offer.guest_user_id != user_id:
            raise WsError(ErrorCode.FORBIDDEN, "This offer is not addressed to you", ref=ref)
        _still_pending(offer, ref=ref)
        if not connection_manager.is_connected(offer.host_device_id):
            raise WsError(ErrorCode.NOT_FOUND, "The host is no longer connected", ref=ref)
        if not await contact_service.are_contacts(db, offer.host_user_id, user_id):
            raise WsError(ErrorCode.NOT_CONTACT, "You are no longer contacts", ref=ref)
        pending = await db.scalar(
            select(ConnectionRequest.id).where(
                ConnectionRequest.guest_device_id == device_id,
                ConnectionRequest.status == RequestStatus.PENDING,
            )
        )
        if pending is not None:
            raise WsError(ErrorCode.REQUEST_PENDING, "This device has a request pending", ref=ref)

        offer.guest_device_id = device_id
        guest = await request_service.load_party(db, user_id, device_id)
        host = await request_service.load_party(db, offer.host_user_id, offer.host_device_id)
        session, key = await request_service.open_session(
            db, offer, guest, host, grant=None, ref=ref
        )
        await db.commit()
        announcement = await request_service.announcement_for(db, session, key)

    scheduler.cancel(timer_key(offer_id))
    await notify.send_to_device(
        host.device.id,
        OfferResult(offer_id=offer_id, accepted=True, session_id=session.id),
    )
    closed = OfferClosed(offer_id=offer_id, reason="accepted_elsewhere")
    for connection in connection_manager.for_user(user_id):
        if connection.device_id != device_id and connection.can(Capability.OFFERS):
            await connection.send(closed)
    await request_service.announce_session(
        session,
        announcement,
        guest=guest,
        host=host,
        runtime=runtime,
        settings=settings,
        initiator=RequestInitiator.HOST,
    )


# ---------------------------------------------------------------- expiry


def _schedule_expiry(offer_id: uuid.UUID, expires_at: datetime) -> None:
    async def fire() -> None:
        await expire(offer_id)

    scheduler.schedule_at(timer_key(offer_id), expires_at, fire)


async def expire(offer_id: uuid.UUID) -> bool:
    async with session_scope() as db:
        offer = await db.get(ConnectionRequest, offer_id)
        if (
            offer is None
            or offer.status != RequestStatus.PENDING
            or offer.initiator != RequestInitiator.HOST
        ):
            return False
        offer.status = RequestStatus.EXPIRED
        offer.responded_at = utcnow()
        await db.commit()
        host_device_id, guest_user_id = offer.host_device_id, offer.guest_user_id
    await notify.send_to_device(
        host_device_id, OfferResult(offer_id=offer_id, accepted=False, reason="expired")
    )
    await notify.send_to_user(
        guest_user_id,
        OfferClosed(offer_id=offer_id, reason="expired"),
        capability=Capability.OFFERS,
    )
    return True
