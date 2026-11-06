# ADR-0018: workspaces - the boundary nothing is shared across

**Status:** accepted on 2026-11-06. Adds "Workspaces" to the [REST contract](../api.md). Phase 1 of the hosted
service.

## Context

Josour is moving from "everyone installs their own server" to a hosted service as well: people download the app, create
an account and work on our server. The first customers are companies whose employees host colleagues in other
countries. On a self-hosted server everybody already trusts the operator and one another; on a shared server, Acme's
staff and a stranger's family must not be able to see or reach each other, and a company needs someone who adds and
removes its people (and, from Phase 2, pays for their seats).

Everything a person can do to another today - see their host, ask for or offer a connection, run a session - already
requires the two to be **contacts** ([ADR-0014](0014-contacts-and-server-held-trust.md)). So the boundary can be drawn
at one place instead of in every query.

## Decision

1. **A `workspaces` table and exactly one workspace per account** (`users.workspace_id`, NOT NULL, plus
   `users.workspace_role`: `owner`, `admin` or `member`). A company, a family and a single person are all a workspace;
   an individual invites their relatives into theirs. One per account is a column, not a memberships table: it makes
   the invariant structural, and a table can come later if belonging to several ever becomes a need.
2. **Contacts never cross a workspace.** A contact invitation is delivered only to an account in the inviter's
   workspace; an invitation to anybody else is written and answered exactly as one to an unused address, and simply
   never arrives. Accepting re-checks it. Requests, offers, sessions and the host list need a contact, so they are
   scoped without being touched.
3. **Moving takes contacts with it.** An account changes workspace only by accepting an invitation, leaving, or being
   removed. Each time, every contact, contact invitation and pending question it had goes first, and a live session
   ends as `contact_removed` - which is exactly what the other side experiences. A workspace left with no accounts at
   all is deleted.
4. **A `workspace_mode` setting**: `single` (the default) keeps every account in one default workspace, which is what a
   self-hosted server always was, and the `/workspace` endpoints answer `403 workspaces_disabled`. `multi` gives every
   new account a workspace of its own; the hosted service runs in this mode. Migration 0004 puts every existing
   account into the default workspace, so an upgraded server behaves exactly as before.
5. **Roles.** Exactly one owner: they change roles and hand the workspace over (`role: owner` makes the caller an
   admin), and must hand it over before leaving or joining another. Admins invite members and remove members and
   rename the workspace; only the owner invites or removes admins. A removed member keeps the account, in a workspace
   of their own.
6. **Workspace invitations are addressed to an email**, like contact invitations: sending one answers the same whoever
   the address is; the address is mailed (when the server sends mail, [ADR-0019](0019-verified-registration.md)); the
   person finds it under `GET /me/workspace-invites` after signing in with that address, and a decline is silent.
7. **The server's own administrators stay above workspaces** (`role = admin` is the platform operator): they list
   workspaces (`GET /admin/workspaces`) and may create an account straight into one.

## Consequences

- Being in the same workspace does not make two people contacts: sharing still needs an invitation and an acceptance.
  That keeps ADR-0014's consent model; a company that wants everyone connected invites everyone. (A per-workspace
  "everyone is a contact" switch can be added later if customers ask.)
- The contacts layer is now the single enforcement point for isolation, so any future feature that lets two people
  interact must go through a contact too, or check `workspace_id` itself.
- Workspaces are where billing attaches in Phase 2 (plan, seats, relay quota).
