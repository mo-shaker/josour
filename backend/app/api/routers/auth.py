from fastapi import APIRouter, Depends, Request, Response, status

from app.api.deps import (
    DbDep,
    SettingsDep,
    client_ip,
    login_rate_limit,
    refresh_rate_limit,
    refund_login_attempt,
)
from app.api.openapi import errors
from app.schemas.auth import LoginRequest, LogoutRequest, RefreshRequest, TokenResponse
from app.services import auth as auth_service
from app.services import tokens as token_service

router = APIRouter(prefix="/auth", tags=["auth"])


@router.post(
    "/login",
    response_model=TokenResponse,
    summary="Sign in and register or verify this device",
    dependencies=[Depends(login_rate_limit)],
    responses=errors(
        401,
        403,
        422,
        429,
        custom={
            401: "`invalid_credentials` for a wrong email or password; `unauthorized` for an "
            "unknown device id or a wrong device secret.",
            403: "`device_revoked` for a revoked device; `account_disabled` for a disabled "
            "account.",
            423: "`account_locked` - 10 failed attempts lock the account for 15 minutes.",
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
    dependencies=[Depends(refresh_rate_limit)],
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
