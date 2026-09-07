"""Relay configuration, from the environment."""

from __future__ import annotations

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    relay_secret: str = Field(min_length=32)
    """Shared with the control-plane API, which mints the session tokens this service verifies.
    Separate from ``JWT_SECRET`` on purpose - see relay/tokens.py."""

    # Named relay_* so the environment variables are RELAY_HOST / RELAY_PORT, matching
    # RELAY_SECRET. Bare `host`/`port` would read HOST and PORT, which is both surprising and
    # dangerous here: a compose file setting RELAY_PORT would be ignored in silence and the
    # service would bind the default instead.
    relay_host: str = "0.0.0.0"  # noqa: S104 - a relay is reachable by definition
    relay_port: int = 443
    """443 because that is the one outbound port open on essentially every network, which is the
    whole reason the relay makes the product work without router configuration (ADR-0009).
    In the container an unprivileged user cannot bind it, so RELAY_PORT is 8443 there and the
    compose file publishes it as 443 on the host."""

    # --- deadlines
    preamble_timeout_seconds: float = Field(default=5.0, gt=0)
    """Matches the client's own 5 s preamble timeout. A peer that connects and says nothing is a
    scanner, and holds a slot until this fires."""
    pair_timeout_seconds: float = Field(default=30.0, gt=0)
    """How long the first arrival waits for its peer before it is told ``no_peer``. Sized to the
    session connect deadline (``connect_timeout_seconds``, 30 s): waiting longer than the control
    plane will wait only holds a slot for a session that is already being failed."""
    idle_timeout_seconds: float = Field(default=120.0, gt=0)
    """A paired session with no bytes in either direction for this long is dropped. The tunnel
    pings every 20 s (docs/protocol.md section 5), so a live session never approaches this."""
    max_session_seconds: float = Field(default=7200.0, gt=0)
    """Hard ceiling per paired session, above the 120-minute ``max_session_minutes`` so that the
    control plane, not the relay, is what normally ends a session."""

    # --- limits
    max_sessions: int = Field(default=64, ge=1)
    """Concurrent paired-or-waiting sessions."""
    max_connections_per_ip: int = Field(default=16, ge=1)
    """Stops one source exhausting the slot table. Both legitimate parties of a session normally
    come from different addresses, so this is generous."""
    max_bytes_per_session: int = Field(default=0, ge=0)
    """0 = unlimited. A ceiling for cost control; the session is dropped once it is crossed."""

    log_level: str = "INFO"
