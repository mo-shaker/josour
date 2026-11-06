from fastapi import APIRouter, Depends, Request, Response, status

from app.api.deps import (
    DbDep,
    SendersDep,
    SettingsDep,
    client_ip,
    client_version_floor,
    login_rate_limit,
    refresh_rate_limit,
    refund_login_attempt,
    register_rate_limit,
    verify_rate_limit,
)
from app.api.openapi import errors
from app.core.errors import ApiError
from app.db.session import commit_retrying_once
from app.models.enums import RegistrationMode
from app.schemas.auth import (
    ConfirmEmailIn,
    EmailIn,
    LoginRequest,
    LogoutRequest,
    PasswordResetIn,
    RefreshRequest,
    RegisterOut,
    RegisterRequest,
    RegistrationInfo,
    SignupPhoneConfirmIn,
    SignupPhoneIn,
    SignupStepOut,
    TokenResponse,
)
from app.services import auth as auth_service
from app.services import outbox, registration, verification
from app.services import tokens as token_service
from app.services.app_settings import settings_service

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
async def registration_info(db: DbDep, senders: SendersDep) -> RegistrationInfo:
    """`closed` - accounts are created by an administrator only; `approval` - anyone may
    register, and an administrator approves the account before it can sign in; `open` - the
    account can sign in at once; `verify` - the account is created once the person has entered
    the code mailed to them (and, with `phone_required`, one sent by SMS). The app shows "Create
    account" unless this is `closed`, and "Forgot password" when `password_reset` is true."""
    app_settings = await settings_service.get(db)
    return RegistrationInfo(
        mode=app_settings.registration_mode,
        phone_required=app_settings.registration_mode == RegistrationMode.VERIFY
        and app_settings.require_phone,
        password_reset=senders.mail_enabled,
    )


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
                503: "`mail_unavailable` / `sms_unavailable` - `verify` mode, but the server "
                "cannot send the codes it needs.",
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
    senders: SendersDep,
) -> RegisterOut:
    """Creates an account from the app (ADR-0017). Never signs in: the app calls
    `POST /auth/login` once the account may be used.

    In `approval` mode the answer is `202 {"status": "pending"}` **whether or not the email
    already had an account**, so registering cannot be used to learn who uses this server; the
    account cannot sign in until an administrator approves it. In `open` mode the answer is
    `201 {"status": "active"}`, and a taken email is a `409` - that mode cannot hide it.

    In `verify` mode (ADR-0019) the answer is `202 {"status": "verify"}`, again whether or not
    the email had an account: no account exists yet, a six-digit code is mailed to the address,
    and `POST /auth/register/confirm` creates the account. If the address already has one, its
    owner is mailed a notice instead of a code.

    Rate limited per client IP."""
    app_settings = await settings_service.get(db)
    if app_settings.registration_mode == RegistrationMode.VERIFY:
        verification.ensure_mail(senders)
        if app_settings.require_phone:
            verification.ensure_sms(senders)
        runtime = getattr(request.app.state, "settings", settings)
        out = await verification.start_signup(
            db,
            secret=runtime.jwt_secret,
            email=payload.email,
            password=payload.password,
            display_name=payload.display_name,
            ip=client_ip(request),
        )
        await db.commit()
        await outbox.deliver(senders, out)
        return RegisterOut(status="verify")

    async def register_once() -> registration.RegistrationOutcome:
        return await registration.register(
            db,
            email=payload.email,
            password=payload.password,
            display_name=payload.display_name,
            ip=client_ip(request),
            # The application's own configuration (tests build apps with their own); the
            # dependency falls back to the process-wide one.
            max_pending=getattr(request.app.state, "settings", settings).max_pending_registrations,
        )

    # Two registrations of one address at the same moment: the second is retried and then
    # answered like any registration of a taken address (202 pending, or 409 in open mode).
    outcome = await commit_retrying_once(db, register_once)
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


# ---------------------------------------------------------------- verified registration (ADR-0019)


async def _verify_mode(db: DbDep) -> bool:
    """The code steps exist only while the server is in ``verify`` mode; a registration begun
    before the operator switched away cannot be finished afterwards."""
    app_settings = await settings_service.get(db)
    if app_settings.registration_mode != RegistrationMode.VERIFY:
        raise ApiError(403, "registration_closed", "Registration is closed on this server")
    return app_settings.require_phone


def _secret(request: Request, settings: SettingsDep) -> str:
    return getattr(request.app.state, "settings", settings).jwt_secret


_CODE_ERRORS = {
    400: "`invalid_code` - wrong, expired, or tried five times already; ask for another.",
    403: "`registration_closed` - the server is not in `verify` mode.",
}


@router.post(
    "/register/resend",
    status_code=status.HTTP_202_ACCEPTED,
    response_class=Response,
    summary="Mail the registration code again",
    dependencies=[Depends(verify_rate_limit)],
    responses=errors(403, 422, 429, custom=_CODE_ERRORS),
)
async def resend_signup_code(
    payload: EmailIn,
    request: Request,
    db: DbDep,
    settings: SettingsDep,
    senders: SendersDep,
) -> Response:
    """`202` whatever happens. A new code goes out only for a registration still waiting for
    one, at most once a minute and five times in all."""
    await _verify_mode(db)
    out = await verification.resend_signup_email(
        db, secret=_secret(request, settings), email=payload.email
    )
    await db.commit()
    await outbox.deliver(senders, out)
    return Response(status_code=status.HTTP_202_ACCEPTED)


@router.post(
    "/register/confirm",
    response_model=SignupStepOut,
    status_code=status.HTTP_201_CREATED,
    summary="Prove the email address and create the account",
    dependencies=[Depends(verify_rate_limit)],
    responses={
        **errors(400, 403, 422, 429, custom=_CODE_ERRORS),
        200: {"model": SignupStepOut, "description": "`phone_required`: the email is proved."},
    },
)
async def confirm_signup_email(
    payload: ConfirmEmailIn,
    request: Request,
    response: Response,
    db: DbDep,
    settings: SettingsDep,
) -> SignupStepOut:
    """`201 {"status": "active"}`: the account exists - sign in with `POST /auth/login`.
    When the server requires a phone, `200 {"status": "phone_required", "signup_token": ...}`
    instead, and the account is created by the phone steps."""
    require_phone = await _verify_mode(db)
    result = await verification.confirm_signup_email(
        db,
        secret=_secret(request, settings),
        email=payload.email,
        code=payload.code,
        require_phone=require_phone,
        ip=client_ip(request),
    )
    await db.commit()
    if result.step == verification.SignupStep.PHONE_REQUIRED:
        response.status_code = status.HTTP_200_OK
    return SignupStepOut(status=result.step.value, signup_token=result.signup_token)


@router.post(
    "/register/phone",
    status_code=status.HTTP_202_ACCEPTED,
    response_class=Response,
    summary="Send the registration code by SMS",
    dependencies=[Depends(verify_rate_limit)],
    responses=errors(
        400,
        403,
        409,
        422,
        429,
        custom={
            400: "`signup_expired` - the token is unknown or the registration expired.",
            403: _CODE_ERRORS[403],
            409: "`phone_in_use` - the number already belongs to an account.",
            429: "`rate_limited` - one code a minute; `too_many_codes` - five in all.",
            503: "`sms_unavailable` - the server does not send text messages.",
        },
    ),
)
async def start_signup_phone(
    payload: SignupPhoneIn,
    request: Request,
    db: DbDep,
    settings: SettingsDep,
    senders: SendersDep,
) -> Response:
    """Sends a six-digit code to the number (asking again with the same number resends it, at
    most once a minute). One account per number."""
    await _verify_mode(db)
    verification.ensure_sms(senders)
    out = await verification.start_signup_phone(
        db, secret=_secret(request, settings), token=payload.signup_token, phone=payload.phone
    )
    await db.commit()
    await outbox.deliver(senders, out)
    return Response(status_code=status.HTTP_202_ACCEPTED)


@router.post(
    "/register/phone/confirm",
    response_model=SignupStepOut,
    status_code=status.HTTP_201_CREATED,
    summary="Prove the phone number and create the account",
    dependencies=[Depends(verify_rate_limit)],
    responses=errors(
        400,
        403,
        409,
        422,
        429,
        custom={
            **_CODE_ERRORS,
            409: "`phone_in_use` - another account took the number meanwhile.",
        },
    ),
)
async def confirm_signup_phone(
    payload: SignupPhoneConfirmIn, request: Request, db: DbDep, settings: SettingsDep
) -> SignupStepOut:
    """`201 {"status": "active"}`: the account exists - sign in with `POST /auth/login`."""
    await _verify_mode(db)
    result = await verification.confirm_signup_phone(
        db,
        secret=_secret(request, settings),
        token=payload.signup_token,
        code=payload.code,
        ip=client_ip(request),
    )
    await db.commit()
    return SignupStepOut(status=result.step.value)


# ---------------------------------------------------------------- password reset (ADR-0019)


@router.post(
    "/password/forgot",
    status_code=status.HTTP_202_ACCEPTED,
    response_class=Response,
    summary="Mail a password reset code",
    dependencies=[Depends(verify_rate_limit)],
    responses=errors(
        422, 429, custom={503: "`mail_unavailable` - the server does not send email."}
    ),
)
async def forgot_password(
    payload: EmailIn,
    request: Request,
    db: DbDep,
    settings: SettingsDep,
    senders: SendersDep,
) -> Response:
    """`202` whether or not the address has an account. If it has one that may sign in, a
    six-digit code valid for 15 minutes is mailed to it (at most one a minute)."""
    verification.ensure_mail(senders)
    out = await verification.request_password_reset(
        db, secret=_secret(request, settings), email=payload.email, ip=client_ip(request)
    )
    await db.commit()
    await outbox.deliver(senders, out)
    return Response(status_code=status.HTTP_202_ACCEPTED)


@router.post(
    "/password/reset",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Set a new password with the mailed code",
    dependencies=[Depends(verify_rate_limit)],
    responses=errors(
        400,
        422,
        429,
        custom={400: "`invalid_code` - wrong, expired, or tried five times already."},
    ),
)
async def reset_password(
    payload: PasswordResetIn, request: Request, db: DbDep, settings: SettingsDep
) -> Response:
    """Sets the password, lifts a lockout, and signs every device out: all refresh tokens and
    dashboard sessions are revoked, so each device signs in again with the new password."""
    await verification.reset_password(
        db,
        secret=_secret(request, settings),
        email=payload.email,
        code=payload.code,
        password=payload.password,
        ip=client_ip(request),
    )
    await db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)
