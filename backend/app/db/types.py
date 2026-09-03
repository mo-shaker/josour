"""Column type helpers shared by all models."""

from sqlalchemy import JSON, DateTime
from sqlalchemy.dialects.postgresql import JSONB

# JSONB on PostgreSQL, plain JSON elsewhere (SQLite in tests).
JSONVariant = JSON().with_variant(JSONB(), "postgresql")

# Always timezone-aware. SQLite stores naive; see app.core.clock.ensure_utc.
UtcDateTime = DateTime(timezone=True)
