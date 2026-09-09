"""Entry point: ``python -m relay``."""

from __future__ import annotations

import asyncio
import contextlib
import logging

from relay.config import Settings
from relay.server import Relay


async def _run() -> None:
    settings = Settings()  # type: ignore[call-arg]  # relay_secret comes from the environment
    logging.basicConfig(
        level=settings.log_level,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    relay = Relay(settings)
    server = await relay.start()
    async with server:
        with contextlib.suppress(asyncio.CancelledError):
            await server.serve_forever()


def main() -> None:
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(_run())


if __name__ == "__main__":
    main()
