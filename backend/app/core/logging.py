"""Structured (JSON lines) logging. Sensitive fields passed via ``extra`` are redacted."""

import json
import logging
import sys
from datetime import UTC, datetime
from typing import Any

_STANDARD_ATTRS = frozenset(vars(logging.LogRecord("", 0, "", 0, "", (), None)).keys()) | {
    "message",
    "asctime",
    "taskName",
}
_SENSITIVE_MARKERS = ("password", "secret", "token", "authorization", "cookie")


def _is_sensitive(key: str) -> bool:
    lowered = key.lower()
    return any(marker in lowered for marker in _SENSITIVE_MARKERS)


class RedactingFilter(logging.Filter):
    """Defence in depth: replaces values of sensitive-looking ``extra`` keys with '***'."""

    def filter(self, record: logging.LogRecord) -> bool:
        for key in list(vars(record)):
            if key not in _STANDARD_ATTRS and _is_sensitive(key):
                setattr(record, key, "***")
        return True


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "ts": datetime.fromtimestamp(record.created, tz=UTC).isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
        }
        for key, value in vars(record).items():
            if key not in _STANDARD_ATTRS:
                payload[key] = value
        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)
        return json.dumps(payload, default=str, ensure_ascii=False)


STATEMENT_LOGGERS = ("sqlalchemy", "sqlalchemy.engine", "aiosqlite", "asyncpg")
"""Libraries that render SQL together with its **bound parameters** at DEBUG.

Those parameters are the browsed host names of ``session_domains``, the presence addresses and
every other row the server touches - exactly what the product document sections 14 and 15 say
must never be logged. They are pinned above DEBUG whatever level the application asks for, so
that ``ENV=dev`` (which runs the root logger at DEBUG) cannot turn the audit log into a
transcript of the database. Raise one deliberately and locally when debugging a query."""


def configure_logging(level: str = "INFO") -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())
    handler.addFilter(RedactingFilter())
    root = logging.getLogger()
    root.handlers[:] = [handler]
    root.setLevel(level.upper())
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        uv = logging.getLogger(name)
        uv.handlers[:] = []
        uv.propagate = True
    for name in STATEMENT_LOGGERS:
        logging.getLogger(name).setLevel(logging.WARNING)
