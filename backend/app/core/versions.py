"""Client version comparison for the ``min_client_version`` floor."""

import re

_LEADING_VERSION = re.compile(r"\d+(?:\.\d+)*")
_USER_AGENT_VERSION = re.compile(r"(?:^|\s)Josour/(\S+)")


def parse_version(value: str | None) -> tuple[int, ...] | None:
    """The leading dotted numbers of ``value`` (``"2.0.1-beta+abc"`` -> ``(2, 0, 1)``), or
    ``None`` when there are none - ``"unknown"``, an empty string, no value at all."""
    if not value:
        return None
    match = _LEADING_VERSION.match(value.strip())
    if match is None:
        return None
    return tuple(int(part) for part in match.group(0).split("."))


def is_below(version: str | None, minimum: str) -> bool:
    """Whether a client reporting ``version`` is older than ``minimum``.

    An empty ``minimum`` means there is no floor. A client that reports no parseable version is
    *not* refused: every Josour build reports one, so its absence means a script or a person
    with curl rather than an outdated app, and the floor exists to steer apps."""
    floor = parse_version(minimum)
    actual = parse_version(version)
    if floor is None or actual is None:
        return False
    width = max(len(floor), len(actual))
    return actual + (0,) * (width - len(actual)) < floor + (0,) * (width - len(floor))


def version_from_user_agent(user_agent: str | None) -> str | None:
    """``Josour/2.0.1 (Windows 11)`` -> ``"2.0.1"``; ``None`` for any other agent."""
    if not user_agent:
        return None
    match = _USER_AGENT_VERSION.search(user_agent)
    return match.group(1) if match else None
