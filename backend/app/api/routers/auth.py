from fastapi import APIRouter, Depends, Request, Response, status

from app.api.deps import (
    DbDep,
    SettingsDep,
    client_ip,
    client_version_floor,
    login_rate_limit,
    refresh_rate_limit,
    refund_login_attempt,
    register_rate_limit,
)
from app.api.openapi import errors
from app.schemas.auth import (
    LoginRequest,
    LogoutRequest,
    RefreshRequest,
    RegisterOut,
    RegisterRequest,
    RegistrationInfo,
    TokenResponse,
)
from app.services import auth as auth_service
from app.services import registration
from app.services import tokens as token_service

router = APIRouter(prefix="/auth", tags=["auth"])


@router.post(
    "/login",
    response_model=TokenResponse,
    summary="Sign in and register or verify this device",
    dependencies=[Depends(login_rate_limit), Depends(client_version_floor)],
    responses=errors(
        401,
        403,
        422,
        429,
        custom={
            401: "`invalid_credentials` for a wrong email or password; `unauthorized` for an "
            "unknown device id or a wrong device secret.",
            403: "`device_revoked` for a revoked device; `account_disabled` for a disabled "
            "account; `account_pending` while a registration waits for approval; "
            "`account_rejected` when it was turned down. The last two are only ever answered "
            "to a correct password.",
            423: "`account_locked` - 10 failed attempts lock the account for 15 minutes.",
            426: "`upgrade_required` - this app is older than the server accepts.",
        },
    ),
)
async def login(
    payload: LoginRequest, request: Request, db: DbDep, settings: SettingsDep
) -> TokenResponse:
    """Authenticates the user **and** the calling device in one step.

    Leave `device.id` and `device.secret` null on a device's first sign-in: the server registers
    it and returns `device.secret` **exactly once** in the response, for the client to store with
    DPAPI. Every later sign-in must present that id and secret.

    Rate limited per client IP and per submitted email (ADR-0008); a successful sign-in does not
    spend the email budget, so only failures count against it.
    """
    response = await auth_service.login(db, settings, payload, client_ip(request))
    refund_login_attempt(request, payload.email)
    return response


@router.post(
    "/refresh",
    response_model=TokenResponse,
    summary="Rotate a refresh token",
    dependencies=[Depends(refresh_rate_limit), Depends(client_version_floor)],
    responses=errors(
        401,
        403,
        422,
        429,
        custom={
            401: "`unauthorized` - unknown, expired, or already-rotated token. Presenting an "
            "already-rotated token is treated as theft: it revokes every live token of that "
            "device and is recorded as `refresh_reuse`.",
            403: "`device_revoked` or `account_disabled`.",
            426: "`upgrade_required` - this app is older than the server accepts.",
        },
    ),
)
async def refresh(
    payload: RefreshRequest, request: Request, db: DbDep, settings: SettingsDep
) -> TokenResponse:
    """Exchanges a refresh token for a fresh pair. The presented token is revoked in the same
    transaction (rotation), so each one is usable once. The response has the same shape as
    `POST /auth/login` but never carries `device.secret`.
    """
    new_refresh, user, device = await token_service.rotate_refresh_token(
        db, settings, payload.refresh_token, client_ip(request)
    )
    return auth_service.build_token_response(settings, user, device, new_refresh)


@router.get(
    "/registration",
    response_model=RegistrationInfo,
    summary="How this server takes new accounts",
    responses=errors(custom={500: "`internal` - unexpected server error."}),
)
async def registration_info(db: DbDep) -> RegistrationInfo:
    """`closed` - accounts are created by an administrator only; `approval` - anyone may
    register, and an administrator approves the account before it can sign in; `open` - the
    account can sign in at once. The app shows "Create account" unless this is `closed`."""
    return RegistrationInfo(mode=await registration.current_mode(db))


@router.post(
    "/register",
    response_model=RegisterOut,
    status_code=status.HTTP_202_ACCEPTED,
    summary="Create an account",
    dependencies=[Depends(register_rate_limit), Depends(client_version_floor)],
    responses={
        **errors(
            403,
            409,
            422,
            429,
            custom={
                403: "`registration_closed` - this server does not take registrations.",
                409: "`conflict` - in `open` mode only: the email already has an account.",
                426: "`upgrade_required` - this app is older than the server accepts.",
                429: "`rate_limited`, or `registration_unavailable` while too many "
                "registrations are waiting for approval.",
            },
        ),
        201: {"model": RegisterOut, "description": "`open` mode: the account is active."},
    },
)
async def register(
    payload: RegisterRequest,
    request: Request,
    response: Response,
    db: DbDep,
    settings: SettingsDep,
) -> RegisterOut:
    """Creates an account from the app (ADR-0017). Never signs in: the app calls
    `POST /auth/login` once the account may be used.

    In `approval` mode the answer is `202 {"status": "pending"}` **whether or not the email
    already had an account**, so registering cannot be used to learn who uses this server; the
    account cannot sign in until an administrator approves it. In `open` mode the answer is
    `201 {"status": "active"}`, and a taken email is a `409` - that mode cannot hide it.

    Rate limited per client IP."""
    outcome = await registration.register(
        db,
        email=payload.email,
        password=payload.password,
        display_name=payload.display_name,
        ip=client_ip(request),
        # The application's own configuration (tests build apps with their own); the
        # dependency falls back to the process-wide one.
        max_pending=getattr(request.app.state, "settings", settings).max_pending_registrations,
    )
    await db.commit()
    if outcome == registration.RegistrationOutcome.ACTIVE:
        response.status_code = status.HTTP_201_CREATED
    return RegisterOut(status=outcome.value)


@router.post(
    "/logout",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Sign out",
    responses=errors(422),
)
async def logout(payload: LogoutRequest, request: Request, db: DbDep) -> Response:
    """Revokes the presented refresh token. Answers `204` whether or not the token was live, so
    it cannot be used to test whether a token exists."""
    await token_service.revoke_refresh_token(db, payload.refresh_token, client_ip(request))
    return Response(status_code=status.HTTP_204_NO_CONTENT)
