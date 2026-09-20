# ADR-0004: non-allow-listed sites go through the user's ordinary connection

**Status:** ~~accepted 2026-09-03~~ — **superseded by [ADR-0010](0010-route-all-through-host.md) on 2026-09-07.**
Everything the work browser asks for now goes through the host; what follows is kept for context.

## Context
Section 6.6 of the product document mentions, in the same breath, "pass the remaining sites through the user's
ordinary connection" and "block domains that are not allow-listed".

## Decision
Inside the work browser: allow-listed domains go through the host, and the local proxy connects to everything else
directly from the user's machine. "Blocking" means blocking passage through the host, and it is enforced on the
host itself (`OPEN_FAIL(not_allowed)`) regardless of what the user's machine decided.

## Consequences
- A complete browsing experience in the work browser, with the host's IP appearing only for the named sites.
- A "block rather than pass through" option becomes a per-company setting in the second release.
