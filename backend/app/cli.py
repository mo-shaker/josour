"""Management CLI (``python manage.py ...`` or ``python -m app.cli ...``)."""

import asyncio
import uuid
from collections.abc import Awaitable, Callable
from typing import Annotated

import typer
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.config import get_settings
from app.core.errors import ApiError
from app.db.session import make_engine, make_sessionmaker
from app.models.enums import SessionStatus, UserRole
from app.schemas.sessions import AdminSessionOut
from app.services import allowlist
from app.services import sessions as session_service
from app.services.users import create_user

cli = typer.Typer(help="RouteBridge management commands.", no_args_is_help=True)


def _run[T](fn: Callable[[AsyncSession], Awaitable[T]]) -> T:
    """Run ``fn`` in a fresh engine/session, commit on success, always dispose."""

    async def runner() -> T:
        engine = make_engine(get_settings().database_url)
        try:
            async with make_sessionmaker(engine)() as db:
                result = await fn(db)
                await db.commit()
                return result
        finally:
            await engine.dispose()

    try:
        return asyncio.run(runner())
    except ApiError as exc:
        typer.secho(f"error: {exc.message}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc


PasswordOpt = Annotated[str, typer.Option(prompt=True, hide_input=True, help="Plain password")]


@cli.command("create-admin")
def create_admin(
    email: Annotated[str, typer.Option(help="Login email")],
    password: PasswordOpt,
    display_name: Annotated[
        str | None, typer.Option(help="Defaults to the email local part")
    ] = None,
) -> None:
    """Create a user with the admin role."""
    _create(email, password, display_name, UserRole.ADMIN)


@cli.command("create-user")
def create_user_cmd(
    email: Annotated[str, typer.Option(help="Login email")],
    password: PasswordOpt,
    display_name: Annotated[
        str | None, typer.Option(help="Defaults to the email local part")
    ] = None,
    role: Annotated[UserRole, typer.Option(help="user or admin")] = UserRole.USER,
) -> None:
    """Create a user (role user by default)."""
    _create(email, password, display_name, role)


def _create(email: str, password: str, display_name: str | None, role: UserRole) -> None:
    name = display_name or email.split("@", 1)[0]

    async def op(db: AsyncSession) -> tuple[str, str]:
        user = await create_user(db, email=email, password=password, display_name=name, role=role)
        return str(user.id), user.email

    user_id, user_email = _run(op)
    typer.echo(f"created {role} {user_email} ({user_id})")


@cli.command("add-domain")
def add_domain(
    entry: Annotated[str, typer.Argument(help="example.com | =exact.com | host:port")],
) -> None:
    """Add an allowlist entry and publish a new allowlist version."""

    async def op(db: AsyncSession) -> tuple[int, int]:
        version = await allowlist.add_domain(db, entry)
        return version.version, len(version.entries)

    version, count = _run(op)
    typer.echo(f"published allowlist version {version} ({count} entries)")


@cli.command("list-domains")
def list_domains() -> None:
    """Print the latest published allowlist version."""

    async def op(db: AsyncSession) -> tuple[int, list[str]]:
        latest = await allowlist.get_latest_version(db)
        return (latest.version, list(latest.entries)) if latest else (0, [])

    version, entries = _run(op)
    typer.echo(f"version {version}")
    for item in entries:
        typer.echo(item)


@cli.command("list-sessions")
def list_sessions(
    status: Annotated[
        SessionStatus | None, typer.Option(help="connecting | active | ended")
    ] = None,
    limit: Annotated[int, typer.Option(min=1, max=500, help="Newest first")] = 20,
) -> None:
    """List sessions (the same rows as ``GET /admin/sessions``), newest first."""

    async def op(db: AsyncSession) -> list[AdminSessionOut]:
        return await session_service.list_all(db, status=status, limit=limit)

    rows = _run(op)
    if not rows:
        typer.echo("no sessions")
        return
    typer.echo(f"{'id':36}  {'status':10}  {'guest -> host':40}  {'ended':22}  reason")
    for row in rows:
        pair = f"{row.guest_display_name}/{row.guest_device_name}"
        pair += f" -> {row.host_display_name}/{row.host_device_name}"
        ended = row.ended_at.isoformat() if row.ended_at else "-"
        typer.echo(
            f"{row.id!s:36}  {row.status:10}  {pair:40.40}  {ended:22}  {row.end_reason or '-'}"
        )


@cli.command("end-session")
def end_session(
    session_id: Annotated[uuid.UUID, typer.Argument(help="Session id to terminate")],
) -> None:
    """Terminate a session centrally: mark it ended, delete its ``session_keys`` secret and
    write the audit row - the same path as ``POST /admin/sessions/{id}/terminate``.

    Note: this runs outside the API process, so the two clients are **not** sent
    ``session.terminate`` here. Prefer the admin endpoint while the server is up; use this when
    it is not, or to be certain the key is gone."""

    async def op(db: AsyncSession) -> str:
        event = await session_service.admin_terminate(
            db, session_id, admin_user_id=None, admin_device_id=None, ip=None
        )
        return event.reason

    reason = _run(op)
    typer.echo(f"session {session_id} ended ({reason}); session key deleted")
    typer.secho(
        "the connected clients were not notified from here; "
        "use POST /admin/sessions/{id}/terminate while the server is running",
        fg=typer.colors.YELLOW,
        err=True,
    )


def main() -> None:
    cli()


if __name__ == "__main__":
    main()
