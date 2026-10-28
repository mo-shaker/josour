"""The one way a connection request or offer leaves ``pending`` (docs/ws-protocol.md section 5).

Accepting, rejecting, cancelling, expiring and the disconnect/contact-removal sweeps can all
race each other: a host accepts in the same instant the guest cancels, or the expiry timer fires
while the host's ``request.accept`` is in flight. Reading ``status == pending`` and then writing
the new status lets both sides win, so both are told contradicting things. Every transition is
therefore a conditional ``UPDATE ... WHERE status = 'pending'``: exactly one caller changes the
row, and the others learn from the row count that they lost. On PostgreSQL the second UPDATE
waits for the first transaction's row lock and then re-checks the condition; on SQLite the
database-level write lock serialises them the same way.
"""

from typing import Any

from sqlalchemy import update
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm.attributes import set_committed_value

from app.core.clock import utcnow
from app.models import ConnectionRequest
from app.models.enums import RequestStatus


async def settle(
    db: AsyncSession, request: ConnectionRequest, status: RequestStatus, **values: Any
) -> bool:
    """Move a pending request/offer to ``status`` (plus ``values``).

    ``False`` means it was no longer pending - somebody else settled it first - and nothing was
    written. Runs in the caller's transaction: the caller commits, and a rollback undoes the
    claim. On success the loaded ``request`` is updated in memory too, without marking it
    dirty."""
    changes = {"status": status, "responded_at": utcnow(), **values}
    result = await db.execute(
        update(ConnectionRequest)
        .where(
            ConnectionRequest.id == request.id,
            ConnectionRequest.status == RequestStatus.PENDING,
        )
        .values(**changes)
        .execution_options(synchronize_session=False)
    )
    if (result.rowcount or 0) != 1:  # type: ignore[attr-defined]
        return False
    for key, value in changes.items():
        set_committed_value(request, key, value)
    return True
