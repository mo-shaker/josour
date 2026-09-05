"""Runtime configuration loaded from environment variables (and an optional .env file)."""

from functools import lru_cache
from typing import Literal

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

Environment = Literal["dev", "test", "prod"]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    database_url: str = "postgresql+asyncpg://routebridge:routebridge@localhost:5433/routebridge"
    database_url_test: str | None = None
    # HS256 key; RFC 7518 requires at least 32 bytes.
    jwt_secret: str = Field(min_length=32)
    access_token_minutes: int = 15
    refresh_token_days: int = 30
    env: Environment = "dev"

    # --- Rate limits (ADR-0008). One switch disables all of them (load harness, test-suite).
    rate_limit_enabled: bool = True
    login_rate_limit_per_minute: int = Field(default=30, ge=1)
    """``POST /auth/login`` per client IP: a coarse anti-flood cap, not the account defence."""
    login_email_rate_limit: int = Field(default=5, ge=1)
    """``POST /auth/login`` per submitted email. Deliberately below ``MAX_FAILED_LOGINS`` (10) so
    the limiter always bites before the account lockout, which an attacker could otherwise use as
    a denial of service. Refunded on a successful login, so only failures spend it."""
    login_email_rate_limit_window_minutes: int = Field(default=15, ge=1)
    refresh_rate_limit_per_minute: int = Field(default=60, ge=1)
    """``POST /auth/refresh`` per client IP; lenient (no KDF on that path)."""
    probe_rate_limit_per_minute: int = Field(default=10, ge=1)
    """``POST /probe`` per user: the server opens a TCP socket to a caller-named public address,
    so this is what stops the endpoint being used as a slow port scanner wearing our identity."""

    # --- Automatic suspicious-device revocation (ADR-0008, part two).
    device_risk_enabled: bool = True
    device_risk_interval_seconds: int = Field(default=300, ge=10)
    device_risk_window_minutes: int = Field(default=60, ge=1)
    device_risk_secret_failures: int = Field(default=10, ge=0)
    """``login_failed``/``device_secret_invalid`` events for one device inside the window that
    revoke it; ``0`` disables the rule. A false revocation locks a real user out of their own
    machine, so the default is set well above anything ordinary use produces."""
    device_risk_refresh_reuse: int = Field(default=3, ge=0)
    """``refresh_reuse`` events for one device inside the window that revoke it; ``0`` disables
    the rule. Above 1 because the first reuse revokes the whole chain, which can legitimately
    bounce one more in-flight token back at us."""

    @property
    def is_dev(self) -> bool:
        return self.env == "dev"


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]  # jwt_secret comes from the environment
