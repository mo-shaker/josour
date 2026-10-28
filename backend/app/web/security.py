"""Who may see the dashboard, and how a form proves it came from the dashboard (ADR-0016).

* **Network**: with ``ADMIN_WEB_ALLOWED_CIDRS`` set, any other address is answered 404 - the
  dashboard does not exist for it.
* **Session**: an opaque token in a ``__Host-`` cookie (HttpOnly, Secure, SameSite=Strict),
  resolved against ``admin_web_sessions``; idle and absolute timeouts; an administrator only.
* **Second factor**: a session that has not passed TOTP reaches only the TOTP page.
* **Forms**: every POST carries the session's CSRF token, and its ``Origin`` must be ours.
"""

import hmac
import ipaddress
from dataclasses import dataclass
from urllib.parse import quote

from fastapi import Request
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.config import Settings, get_settings
from app.models import AdminWebSession, User
from app.services import admin_web_sessions
from app.web.i18n import DEFAULT_LANGUAGE, LANGUAGES

SESSION_COOKIE = "__Host-josour_admin"
LANGUAGE_COOKIE = "josour_admin_lang"


class WebRedirect(Exception):
    def __init__(self, url: str) -> None:
        super().__init__(url)
        self.url = url


class WebNotFound(Exception):
    pass


class WebForbidden(Exception):
    pass


@dataclass(slots=True)
class WebContext:
    user: User
    session: AdminWebSession
    lang: str

    @property
    def csrf(self) -> str:
        return self.session.csrf_token


def runtime(request: Request) -> Settings:
    return getattr(request.app.state, "settings", None) or get_settings()


def language(request: Request) -> str:
    value = request.cookies.get(LANGUAGE_COOKIE, DEFAULT_LANGUAGE)
    return value if value in LANGUAGES else DEFAULT_LANGUAGE


def client_ip(request: Request) -> str | None:
    """As uvicorn's proxy-headers middleware resolved it (see the Dockerfile CMD)."""
    return request.client.host if request.client else None


def network_allowed(request: Request) -> bool:
    """``ADMIN_WEB_ALLOWED_CIDRS`` (empty allows everyone)."""
    networks = runtime(request).admin_web_networks
    if not networks:
        return True
    try:
        address = ipaddress.ip_address(client_ip(request) or "")
    except ValueError:
        return False
    return any(address in network for network in networks)


def check_network(request: Request) -> None:
    if not network_allowed(request):
        raise WebNotFound()


def expected_origin(request: Request) -> str:
    configured = runtime(request).public_origin.rstrip("/")
    if configured:
        return configured
    host = request.headers.get("host", "")
    return f"{request.url.scheme}://{host}"


def check_origin(request: Request) -> None:
    """Refuse a form that a browser says came from another site.

    ``Origin`` is the first witness. A browser replaces it with ``null`` when the page's referrer
    policy is ``no-referrer`` - which is why the dashboard sends ``Referrer-Policy: same-origin``
    (``app.web.SECURITY_HEADERS``) and Caddy only adds ``no-referrer`` where the application set
    none - and in a few other cases, so ``Sec-Fetch-Site`` (sent by every current browser, and
    not settable by a page) is the second. Only a request carrying neither falls back on the
    CSRF token and the SameSite cookie alone; a ``null`` origin with nothing else to vouch for it
    (a sandboxed frame) is refused."""
    origin = request.headers.get("origin")
    if origin and origin != "null":
        if origin.rstrip("/") != expected_origin(request):
            raise WebForbidden()
        return
    site = request.headers.get("sec-fetch-site")
    if site is not None:
        if site != "same-origin":
            raise WebForbidden()
        return
    if origin == "null":
        raise WebForbidden()


async def current(
    request: Request, db: AsyncSession, *, allow_without_mfa: bool = False
) -> WebContext:
    """The signed-in administrator, or a redirect to sign in."""
    check_network(request)
    settings = runtime(request)
    resolved = await admin_web_sessions.resolve(
        db, request.cookies.get(SESSION_COOKIE), idle_minutes=settings.admin_web_idle_minutes
    )
    await db.commit()
    if resolved is None:
        target = request.url.path
        if request.url.query:
            target += "?" + request.url.query
        raise WebRedirect(f"/admin/login?next={quote(target, safe='')}")
    session, user = resolved
    if not allow_without_mfa and not mfa_satisfied(session, user, settings):
        raise WebRedirect("/admin/login/totp")
    return WebContext(user=user, session=session, lang=language(request))


def mfa_satisfied(session: AdminWebSession, user: User, settings: Settings) -> bool:
    """A code was verified in this session, or none is needed: the administrator has no TOTP
    and the server does not require one. Evaluated on every request, so enrolling TOTP or
    turning on ``ADMIN_WEB_REQUIRE_TOTP`` reaches sessions that already exist."""
    if session.mfa_passed:
        return True
    return user.totp_enabled_at is None and not settings.admin_web_require_totp


async def verified_form(request: Request, ctx: WebContext) -> dict[str, str]:
    """The submitted form, after the CSRF token and the origin both check out."""
    check_origin(request)
    form = await request.form()
    token = form.get("csrf")
    if not isinstance(token, str) or not hmac.compare_digest(token, ctx.csrf):
        raise WebForbidden()
    return {key: value for key, value in form.items() if isinstance(value, str)}


def safe_next(value: str | None) -> str:
    """Only ever back into the dashboard: an open redirect would make our sign-in page a
    convincing first step for somebody else's phishing."""
    if value and value.startswith("/admin") and not value.startswith("//"):
        return value
    return "/admin"
