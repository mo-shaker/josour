"""The dashboard's pages (ADR-0016). Server-rendered, plain forms, no JavaScript.

Every change is a POST that redirects to a GET (so a reload never repeats it), and every action
that cannot be undone goes through a confirmation page first. The actions themselves are the
same ``app.services`` functions the admin API calls, followed by the same after-commit effects,
so the browser and the API cannot drift apart; each is audited with ``via: "web"``.
"""

import re
import uuid
from datetime import datetime
from pathlib import Path
from typing import Any
from urllib.parse import quote

from fastapi import APIRouter, Request
from fastapi.responses import HTMLResponse, RedirectResponse, Response
from fastapi.templating import Jinja2Templates
from jinja2 import pass_context
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core import totp
from app.core.clock import ensure_utc, utcnow
from app.core.errors import ApiError, NotFound
from app.core.rate_limit import opaque_key
from app.core.security import verify_password_async
from app.db.session import get_db
from app.models import Device, Session, User
from app.models.enums import (
    NON_ENDED_SESSION_STATUSES,
    InviteStatus,
    SecurityEventType,
    UserRole,
)
from app.schemas.admin import AdminUserPatch
from app.schemas.settings import SettingsPatch
from app.services import admin_web_sessions, allowlist, effects
from app.services import auth as auth_service
from app.services import contacts as contact_service
from app.services import devices as device_service
from app.services import diagnostics as diagnostics_service
from app.services import sessions as session_service
from app.services import users as user_service
from app.services.app_settings import settings_service
from app.services.effects import Effects
from app.services.events import event_bus
from app.services.security_events import record_event
from app.web.i18n import LANGUAGES, STRINGS, translate
from app.web.security import (
    LANGUAGE_COOKIE,
    SESSION_COOKIE,
    WebContext,
    check_network,
    check_origin,
    client_ip,
    current,
    language,
    mfa_satisfied,
    runtime,
    safe_next,
    verified_form,
)
from app.ws.connection_manager import connection_manager

router = APIRouter(prefix="/admin", include_in_schema=False)
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))
PAGE_SIZE = 50
_CODE = re.compile(r"[a-z_]{1,40}")


@pass_context
def _t(context: Any, key: str, **values: object) -> str:
    return translate(key, context["lang"], **values)


def _datetime(value: datetime | None) -> str:
    aware = ensure_utc(value)
    return aware.strftime("%Y-%m-%d %H:%M UTC") if aware else "—"


def _bytes(value: int | None) -> str:
    size = float(value or 0)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024:
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"


templates.env.globals["t"] = _t
templates.env.filters["dt"] = _datetime
templates.env.filters["bytes"] = _bytes


class _Db:
    """``async with _Db(request) as db`` - a session from ``get_db`` outside FastAPI's DI, so
    the dashboard uses exactly the database the API does (tests override ``get_db``)."""

    def __init__(self, request: Request) -> None:
        self._gen = request.app.dependency_overrides.get(get_db, get_db)()

    async def __aenter__(self) -> AsyncSession:
        return await self._gen.__anext__()

    async def __aexit__(self, *_: object) -> None:
        await self._gen.aclose()


async def _page(
    request: Request,
    db: AsyncSession,
    ctx: WebContext | None,
    template: str,
    *,
    status_code: int = 200,
    **values: Any,
) -> HTMLResponse:
    lang = ctx.lang if ctx else language(request)
    done = request.query_params.get("done")
    error = request.query_params.get("error")
    pending = 0
    if ctx is not None:
        pending = int(
            await db.scalar(
                select(func.count())
                .select_from(User)
                .where(user_service._state_filter(user_service.UserState.PENDING))
            )
            or 0
        )
    return templates.TemplateResponse(
        request,
        template,
        {
            "lang": lang,
            "dir": "rtl" if lang == "ar" else "ltr",
            "ctx": ctx,
            "csrf": ctx.csrf if ctx else "",
            "pending_count": pending,
            "done": f"done.{done}" if done and f"done.{done}" in STRINGS else None,
            "error": error if error and _CODE.fullmatch(error) else None,
            "path": request.url.path,
            **values,
        },
        status_code=status_code,
    )


def _back(url: str, **params: str) -> RedirectResponse:
    query = "&".join(f"{key}={quote(value, safe='')}" for key, value in params.items())
    separator = "&" if "?" in url else "?"
    return RedirectResponse(url + (separator + query if query else ""), status_code=303)


async def _act(
    request: Request, ctx: WebContext, back: str, done: str, action: Any
) -> RedirectResponse:
    """Run one admin action: its own transaction, then its effects, then back to a page."""
    async with _Db(request) as db:
        try:
            result = await action(db)
            await db.commit()
        except ApiError as exc:
            await db.rollback()
            return _back(back, error=exc.code)
    if isinstance(result, Effects):
        await effects.apply(result)
    return _back(back, done=done)


# ---------------------------------------------------------------- sign-in


@router.get("/login")
async def login_page(request: Request) -> Response:
    check_network(request)
    async with _Db(request) as db:
        return await _page(
            request,
            db,
            None,
            "login.html",
            next=safe_next(request.query_params.get("next")),
            message=None,
        )


@router.post("/login")
async def login(request: Request) -> Response:
    check_network(request)
    check_origin(request)
    settings = runtime(request)
    form = await request.form()
    email = str(form.get("email") or "").strip().lower()[:320]
    password = str(form.get("password") or "")[:1024]
    target = safe_next(str(form.get("next") or ""))
    limiters = getattr(request.app.state, "rate_limiters", None)

    async def fail(message: str, status_code: int) -> Response:
        async with _Db(request) as db:
            return await _page(
                request,
                db,
                None,
                "login.html",
                status_code=status_code,
                next=target,
                message=message,
                email=email,
            )

    if limiters is not None:
        for limiter, key in (
            (limiters.admin_login_ip, client_ip(request) or "unknown"),
            (limiters.login_email, opaque_key(email)),
        ):
            if not limiter.check(key).allowed:
                return await fail("login.rate_limited", 429)

    async with _Db(request) as db:
        try:
            user = await auth_service.authenticate_password(
                db, email, password, client_ip(request), channel="web"
            )
        except ApiError as exc:
            message = "login.locked" if exc.code == "account_locked" else "login.failed"
            return await fail(message, 401 if exc.status_code == 401 else 403)
        if user.role != UserRole.ADMIN:
            # The same answer as a wrong password: the dashboard does not say who has an account.
            record_event(
                db,
                SecurityEventType.ADMIN_WEB_LOGIN_FAILED,
                user_id=user.id,
                ip=client_ip(request),
                details={"reason": "not_admin"},
            )
            await db.commit()
            return await fail("login.failed", 401)
        await auth_service.mark_signed_in(user, password)
        needs_second_factor = user.totp_enabled_at is not None or settings.admin_web_require_totp
        issued = await admin_web_sessions.create(
            db,
            user,
            mfa_passed=not needs_second_factor,
            ip=client_ip(request),
            user_agent=request.headers.get("user-agent"),
            absolute_hours=settings.admin_web_absolute_hours,
        )
        if not needs_second_factor:
            record_event(
                db,
                SecurityEventType.ADMIN_WEB_LOGIN_SUCCESS,
                user_id=user.id,
                ip=client_ip(request),
            )
        await db.commit()
    if limiters is not None:
        limiters.login_email.refund(opaque_key(email))
    response = RedirectResponse(
        target if not needs_second_factor else "/admin/login/totp", status_code=303
    )
    _set_session_cookie(response, issued.token, settings.admin_web_absolute_hours)
    return response


def _set_session_cookie(response: Response, token: str, hours: int) -> None:
    response.set_cookie(
        SESSION_COOKIE,
        token,
        max_age=hours * 3600,
        path="/",
        secure=True,
        httponly=True,
        samesite="strict",
    )


@router.get("/login/totp")
async def totp_page(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db, allow_without_mfa=True)
        if mfa_satisfied(ctx.session, ctx.user, runtime(request)):
            return RedirectResponse("/admin", status_code=303)
        enrolling = ctx.user.totp_enabled_at is None
        if enrolling and ctx.user.totp_secret is None:
            ctx.user.totp_secret = totp.generate_secret()
            await db.commit()
        return await _page(
            request,
            db,
            ctx,
            "totp.html",
            enrolling=enrolling,
            secret=ctx.user.totp_secret if enrolling else None,
            uri=totp.provisioning_uri(ctx.user.totp_secret, account=ctx.user.email)
            if enrolling and ctx.user.totp_secret
            else None,
            wrong=request.query_params.get("wrong") == "1",
        )


@router.post("/login/totp")
async def totp_submit(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db, allow_without_mfa=True)
        form = await verified_form(request, ctx)
        user = ctx.user
        secret = user.totp_secret
        step = (
            totp.verify(secret, form.get("code", ""), last_step=user.totp_last_step)
            if secret
            else None
        )
        if step is None:
            record_event(
                db,
                SecurityEventType.ADMIN_WEB_LOGIN_FAILED,
                user_id=user.id,
                ip=client_ip(request),
                details={"reason": "totp"},
            )
            await db.commit()
            return RedirectResponse("/admin/login/totp?wrong=1", status_code=303)
        user.totp_last_step = step
        if user.totp_enabled_at is None:
            user.totp_enabled_at = utcnow()
            record_event(db, SecurityEventType.TOTP_ENABLED, user_id=user.id, ip=client_ip(request))
        ctx.session.mfa_passed = True
        token = await admin_web_sessions.rotate(db, ctx.session)
        record_event(
            db, SecurityEventType.ADMIN_WEB_LOGIN_SUCCESS, user_id=user.id, ip=client_ip(request)
        )
        await db.commit()
    response = RedirectResponse("/admin", status_code=303)
    _set_session_cookie(response, token, runtime(request).admin_web_absolute_hours)
    return response


@router.post("/logout")
async def logout(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db, allow_without_mfa=True)
        await verified_form(request, ctx)
        await admin_web_sessions.revoke(db, ctx.session)
        record_event(
            db, SecurityEventType.ADMIN_WEB_LOGOUT, user_id=ctx.user.id, ip=client_ip(request)
        )
        await db.commit()
    response = RedirectResponse("/admin/login", status_code=303)
    response.delete_cookie(SESSION_COOKIE, path="/", secure=True, httponly=True, samesite="strict")
    return response


@router.get("/lang/{lang}")
async def set_language(request: Request, lang: str) -> Response:
    check_network(request)
    response = RedirectResponse(safe_next(request.query_params.get("next")), status_code=303)
    if lang in LANGUAGES:
        response.set_cookie(
            LANGUAGE_COOKIE, lang, max_age=365 * 86400, path="/admin", samesite="lax", secure=True
        )
    return response


# ---------------------------------------------------------------- overview


@router.get("")
async def overview(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        counts = await user_service.count_by_state(db)
        live = int(
            await db.scalar(
                select(func.count())
                .select_from(Session)
                .where(Session.status.in_(NON_ENDED_SESSION_STATUSES))
            )
            or 0
        )
        events = await session_service.list_security_events(db, limit=10)
        return await _page(
            request,
            db,
            ctx,
            "overview.html",
            counts=counts,
            live_sessions=live,
            online_devices=len(connection_manager),
            events=events,
        )


# ---------------------------------------------------------------- users


@router.get("/users")
async def users_list(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        q = (request.query_params.get("q") or "").strip()[:320]
        state_value = request.query_params.get("state") or ""
        state = (
            user_service.UserState(state_value)
            if state_value in {s.value for s in user_service.UserState}
            else None
        )
        try:
            page = max(0, int(request.query_params.get("page") or 0))
        except ValueError:
            page = 0
        rows = await user_service.list_users(
            db, q=q or None, state=state, limit=PAGE_SIZE + 1, offset=page * PAGE_SIZE
        )
        return await _page(
            request,
            db,
            ctx,
            "users.html",
            users=rows[:PAGE_SIZE],
            has_next=len(rows) > PAGE_SIZE,
            page=page,
            q=q,
            state=state.value if state else "",
            states=[s.value for s in user_service.UserState],
        )


@router.get("/users/new")
async def user_new_page(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        return await _page(request, db, ctx, "user_new.html")


@router.post("/users/new")
async def user_new(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
        role = UserRole.ADMIN if form.get("role") == "admin" else UserRole.USER
        try:
            user = await user_service.create_user(
                db,
                email=form.get("email", ""),
                password=form.get("password", ""),
                display_name=form.get("display_name", ""),
                role=role,
            )
            await db.commit()
        except ApiError as exc:
            await db.rollback()
            return _back("/admin/users/new", error=exc.code)
    return _back(f"/admin/users/{user.id}", done="created")


async def _user(db: AsyncSession, user_id: uuid.UUID) -> User:
    user = await db.get(User, user_id)
    if user is None:
        raise NotFound("User not found")
    return user


@router.get("/users/{user_id}")
async def user_detail(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        try:
            user = await _user(db, user_id)
        except NotFound:
            return await _not_found(request, db, ctx)
        devices = await device_service.list_devices(db, user_id=user.id, limit=100)
        contacts = await contact_service.list_for_admin(db, user.id)
        sessions = await session_service.list_for_user(db, user.id, limit=20)
        events = await session_service.list_security_events(db, user_id=user.id, limit=20)
        return await _page(
            request,
            db,
            ctx,
            "user_detail.html",
            user=user,
            devices=devices,
            online={d.id for d in devices if connection_manager.is_connected(d.id)},
            contacts=contacts,
            sessions=sessions,
            events=events,
            is_self=user.id == ctx.user.id,
        )


async def _patch(
    request: Request, user_id: uuid.UUID, build: Any, done: str = "saved"
) -> RedirectResponse:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
    back = f"/admin/users/{user_id}"

    async def action(db: AsyncSession) -> Effects:
        user = await _user(db, user_id)
        patch = AdminUserPatch(**build(form))
        disconnect = await user_service.update_user(
            db, user, patch, actor_user_id=ctx.user.id, ip=client_ip(request)
        )
        return Effects(disconnect=disconnect)

    try:
        return await _act(request, ctx, back, done, action)
    except ValueError:
        return _back(back, error="validation_error")


@router.post("/users/{user_id}/profile")
async def user_profile(request: Request, user_id: uuid.UUID) -> Response:
    return await _patch(
        request,
        user_id,
        lambda f: {
            "display_name": f.get("display_name") or None,
            "email": f.get("email") or None,
            "role": f.get("role") or None,
        },
    )


@router.post("/users/{user_id}/password")
async def user_password(request: Request, user_id: uuid.UUID) -> Response:
    return await _patch(
        request, user_id, lambda f: {"password": f.get("password", "")}, done="password_set"
    )


@router.post("/users/{user_id}/activate")
async def user_activate(request: Request, user_id: uuid.UUID) -> Response:
    return await _patch(request, user_id, lambda _: {"is_active": True})


@router.post("/users/{user_id}/deactivate")
async def user_deactivate(request: Request, user_id: uuid.UUID) -> Response:
    return await _patch(request, user_id, lambda _: {"is_active": False})


@router.post("/users/{user_id}/unlock")
async def user_unlock(request: Request, user_id: uuid.UUID) -> Response:
    return await _patch(request, user_id, lambda _: {"unlock": True})


@router.post("/users/{user_id}/approve")
async def user_approve(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)

    async def action(db: AsyncSession) -> None:
        await user_service.approve_user(
            db, await _user(db, user_id), actor_user_id=ctx.user.id, ip=client_ip(request)
        )

    return await _act(request, ctx, safe_next(form.get("next")), "approved", action)


# --- confirmed actions: a GET shows what will happen, the POST does it.


async def _confirm(
    request: Request, message: str, action: str, back: str, **values: object
) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        return await _page(
            request,
            db,
            ctx,
            "confirm.html",
            message=translate(message, ctx.lang, **values),
            action=action,
            back=back,
        )


@router.get("/users/{user_id}/reject")
async def user_reject_confirm(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        user = await db.get(User, user_id)
        name = user.display_name if user else "?"
    return await _confirm(
        request,
        "confirm.reject_user",
        f"/admin/users/{user_id}/reject",
        "/admin/registrations",
        name=name,
    )


@router.post("/users/{user_id}/reject")
async def user_reject(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)

    async def action(db: AsyncSession) -> None:
        await user_service.reject_user(
            db, await _user(db, user_id), actor_user_id=ctx.user.id, ip=client_ip(request)
        )

    return await _act(request, ctx, "/admin/registrations", "rejected", action)


@router.get("/users/{user_id}/delete")
async def user_delete_confirm(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        user = await db.get(User, user_id)
        name = user.display_name if user else "?"
    return await _confirm(
        request,
        "confirm.delete_user",
        f"/admin/users/{user_id}/delete",
        f"/admin/users/{user_id}",
        name=name,
    )


@router.post("/users/{user_id}/delete")
async def user_delete(request: Request, user_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)

    async def action(db: AsyncSession) -> Effects:
        return await user_service.delete_user(
            db, await _user(db, user_id), actor_user_id=ctx.user.id, ip=client_ip(request)
        )

    return await _act(request, ctx, f"/admin/users/{user_id}", "deleted", action)


@router.get("/devices/{device_id}/revoke")
async def device_revoke_confirm(request: Request, device_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        device = await db.get(Device, device_id)
        name, owner = (device.name, device.user_id) if device else ("?", None)
    return await _confirm(
        request,
        "confirm.revoke_device",
        f"/admin/devices/{device_id}/revoke",
        f"/admin/users/{owner}" if owner else "/admin/users",
        name=name,
    )


@router.post("/devices/{device_id}/revoke")
async def device_revoke(request: Request, device_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)
        device = await db.get(Device, device_id)
        owner = device.user_id if device else None

    async def action(db: AsyncSession) -> Effects:
        row = await db.get(Device, device_id)
        if row is None:
            raise NotFound("Device not found")
        changed = await device_service.revoke_device(
            db, row, by="admin", actor_user_id=ctx.user.id, ip=client_ip(request)
        )
        return Effects(disconnect=[row.id] if changed else [])

    back = f"/admin/users/{owner}" if owner else "/admin/users"
    return await _act(request, ctx, back, "revoked", action)


@router.get("/contacts/{user_id}/{contact_id}/remove")
async def contact_remove_confirm(
    request: Request, user_id: uuid.UUID, contact_id: uuid.UUID
) -> Response:
    async with _Db(request) as db:
        a, b = await db.get(User, user_id), await db.get(User, contact_id)
        names = (a.display_name if a else "?", b.display_name if b else "?")
    return await _confirm(
        request,
        "confirm.remove_contact",
        f"/admin/contacts/{user_id}/{contact_id}/remove",
        f"/admin/users/{user_id}",
        a=names[0],
        b=names[1],
    )


@router.post("/contacts/{user_id}/{contact_id}/remove")
async def contact_remove(request: Request, user_id: uuid.UUID, contact_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)

    async def action(db: AsyncSession) -> Effects:
        return await contact_service.remove_contact(
            db, user_id, contact_id, actor_user_id=ctx.user.id, ip=client_ip(request)
        )

    return await _act(request, ctx, f"/admin/users/{user_id}", "contact_removed", action)


@router.post("/contacts/{user_id}/{contact_id}/revoke-trust")
async def contact_revoke_trust(
    request: Request, user_id: uuid.UUID, contact_id: uuid.UUID
) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)

    async def action(db: AsyncSession) -> Effects:
        return await contact_service.set_trust(
            db, user_id, contact_id, False, actor_user_id=ctx.user.id, ip=client_ip(request)
        )

    return await _act(request, ctx, f"/admin/users/{user_id}", "trust_revoked", action)


# ---------------------------------------------------------------- registrations, invitations


@router.get("/registrations")
async def registrations(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        rows = await user_service.list_users(db, state=user_service.UserState.PENDING, limit=500)
        return await _page(request, db, ctx, "registrations.html", users=list(reversed(rows)))


@router.get("/invites")
async def invites(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        status_value = request.query_params.get("status") or ""
        status = (
            InviteStatus(status_value) if status_value in {s.value for s in InviteStatus} else None
        )
        rows = await contact_service.list_invites_for_admin(db, status=status, limit=200)
        return await _page(
            request,
            db,
            ctx,
            "invites.html",
            invites=rows,
            status=status.value if status else "",
            statuses=[s.value for s in InviteStatus],
        )


# ---------------------------------------------------------------- sessions


@router.get("/sessions")
async def sessions(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        live_only = request.query_params.get("live") == "1"
        rows = await session_service.list_all(db, limit=200)
        if live_only:
            rows = [r for r in rows if r.status in NON_ENDED_SESSION_STATUSES]
        return await _page(request, db, ctx, "sessions.html", sessions=rows, live_only=live_only)


@router.get("/sessions/{session_id}/terminate")
async def session_terminate_confirm(request: Request, session_id: uuid.UUID) -> Response:
    return await _confirm(
        request,
        "confirm.terminate_session",
        f"/admin/sessions/{session_id}/terminate",
        "/admin/sessions?live=1",
    )


@router.post("/sessions/{session_id}/terminate")
async def session_terminate(request: Request, session_id: uuid.UUID) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)

    async def action(db: AsyncSession) -> Effects:
        event = await session_service.admin_terminate(
            db,
            session_id,
            admin_user_id=ctx.user.id,
            admin_device_id=None,
            ip=client_ip(request),
            via="web",
        )
        return Effects(session_events=[event])

    return await _act(request, ctx, "/admin/sessions?live=1", "terminated", action)


# ---------------------------------------------------------------- security log, diagnostics


@router.get("/events")
async def events(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        event_type = request.query_params.get("type") or ""
        known = {t.value for t in SecurityEventType}
        rows = await session_service.list_security_events(
            db, event_type=event_type if event_type in known else None, limit=200
        )
        return await _page(
            request,
            db,
            ctx,
            "events.html",
            events=rows,
            event_type=event_type if event_type in known else "",
            types=sorted(known),
        )


@router.get("/diagnostics")
async def diagnostics(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        summary = await diagnostics_service.summarize(db)
        return await _page(request, db, ctx, "diagnostics.html", summary=summary)


# ---------------------------------------------------------------- settings, allow-list


@router.get("/settings")
async def settings_page(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        current_settings = await settings_service.get(db)
        return await _page(request, db, ctx, "settings.html", settings=current_settings)


@router.post("/settings")
async def settings_save(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
    try:
        patch = SettingsPatch(
            registration_mode=form.get("registration_mode") or None,
            max_session_minutes=int(form["max_session_minutes"]),
            request_timeout_seconds=int(form["request_timeout_seconds"]),
            connect_timeout_seconds=int(form["connect_timeout_seconds"]),
            log_domains=form.get("log_domains") == "on",
            enforce_allowlist=form.get("enforce_allowlist") == "on",
            allowed_ports=[int(p) for p in re.split(r"[,\s]+", form.get("allowed_ports", "")) if p],
            min_client_version=form.get("min_client_version", ""),
            invite_ttl_days=int(form["invite_ttl_days"]),
        )
    except (KeyError, ValueError):
        return _back("/admin/settings", error="validation_error")

    async def action(db: AsyncSession) -> None:
        await settings_service.update(db, patch, actor_user_id=ctx.user.id, ip=client_ip(request))

    return await _act(request, ctx, "/admin/settings", "saved", action)


@router.get("/domains")
async def domains_page(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        latest = await allowlist.get_latest_version(db)
        return await _page(request, db, ctx, "domains.html", latest=latest)


@router.post("/domains")
async def domains_save(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
    entries = [line.strip() for line in form.get("entries", "").splitlines() if line.strip()]
    published: list[Any] = []

    async def action(db: AsyncSession) -> None:
        published.append(await allowlist.replace_entries(db, entries, created_by=ctx.user.id))

    redirect = await _act(request, ctx, "/admin/domains", "published", action)
    if published:
        await event_bus.publish(allowlist.published_event(published[0]))
    return redirect


# ---------------------------------------------------------------- my account


@router.get("/account")
async def account(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        enrolling = ctx.user.totp_enabled_at is None and ctx.user.totp_secret is not None
        return await _page(
            request,
            db,
            ctx,
            "account.html",
            enrolling=enrolling,
            uri=totp.provisioning_uri(ctx.user.totp_secret, account=ctx.user.email)
            if enrolling and ctx.user.totp_secret
            else None,
        )


@router.post("/account/password")
async def account_password(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
        if not await verify_password_async(ctx.user.password_hash, form.get("current", "")):
            return _back("/admin/account", error="wrong_password")
        try:
            await user_service.update_user(
                db,
                ctx.user,
                AdminUserPatch(password=form.get("password", "")),
                actor_user_id=ctx.user.id,
                ip=client_ip(request),
            )
        except (ApiError, ValueError):
            await db.rollback()
            return _back("/admin/account", error="validation_error")
        await db.commit()
    # Changing the password ended every dashboard session, this one included.
    return RedirectResponse("/admin/login", status_code=303)


@router.post("/account/totp/start")
async def account_totp_start(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        await verified_form(request, ctx)
        if ctx.user.totp_enabled_at is None:
            ctx.user.totp_secret = totp.generate_secret()
            await db.commit()
    return RedirectResponse("/admin/account", status_code=303)


@router.post("/account/totp/confirm")
async def account_totp_confirm(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
        user = ctx.user
        if user.totp_secret is None or user.totp_enabled_at is not None:
            return RedirectResponse("/admin/account", status_code=303)
        step = totp.verify(user.totp_secret, form.get("code", ""), last_step=user.totp_last_step)
        if step is None:
            return _back("/admin/account", error="totp")
        user.totp_last_step = step
        user.totp_enabled_at = utcnow()
        ctx.session.mfa_passed = True
        record_event(db, SecurityEventType.TOTP_ENABLED, user_id=user.id, ip=client_ip(request))
        await db.commit()
    return _back("/admin/account", done="saved")


@router.post("/account/totp/disable")
async def account_totp_disable(request: Request) -> Response:
    async with _Db(request) as db:
        ctx = await current(request, db)
        form = await verified_form(request, ctx)
        if runtime(request).admin_web_require_totp:
            return _back("/admin/account", error="forbidden")
        if not await verify_password_async(ctx.user.password_hash, form.get("current", "")):
            return _back("/admin/account", error="wrong_password")
        ctx.user.totp_secret = None
        ctx.user.totp_enabled_at = None
        ctx.user.totp_last_step = None
        record_event(
            db, SecurityEventType.TOTP_DISABLED, user_id=ctx.user.id, ip=client_ip(request)
        )
        await db.commit()
    return _back("/admin/account", done="saved")


# ---------------------------------------------------------------- errors


async def _not_found(request: Request, db: AsyncSession, ctx: WebContext | None) -> HTMLResponse:
    return await _page(request, db, ctx, "error.html", status_code=404, message="error.not_found")


async def render_error(request: Request, message: str, status_code: int) -> HTMLResponse:
    async with _Db(request) as db:
        return await _page(
            request, db, None, "error.html", status_code=status_code, message=message
        )
