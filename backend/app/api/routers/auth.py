from fastapi import APIRouter, Depends, Request, Response, status

from app.api.deps import DbDep, SettingsDep, client_ip, login_rate_limit
from app.schemas.auth import LoginRequest, LogoutRequest, RefreshRequest, TokenResponse
from app.services import auth as auth_service
from app.services import tokens as token_service

router = APIRouter(prefix="/auth", tags=["auth"])


@router.post("/login", response_model=TokenResponse, dependencies=[Depends(login_rate_limit)])
async def login(
    payload: LoginRequest, request: Request, db: DbDep, settings: SettingsDep
) -> TokenResponse:
    return await auth_service.login(db, settings, payload, client_ip(request))


@router.post("/refresh", response_model=TokenResponse)
async def refresh(
    payload: RefreshRequest, request: Request, db: DbDep, settings: SettingsDep
) -> TokenResponse:
    new_refresh, user, device = await token_service.rotate_refresh_token(
        db, settings, payload.refresh_token, client_ip(request)
    )
    return auth_service.build_token_response(settings, user, device, new_refresh)


@router.post("/logout", status_code=status.HTTP_204_NO_CONTENT, response_class=Response)
async def logout(payload: LogoutRequest, request: Request, db: DbDep) -> Response:
    await token_service.revoke_refresh_token(db, payload.refresh_token, client_ip(request))
    return Response(status_code=status.HTTP_204_NO_CONTENT)
