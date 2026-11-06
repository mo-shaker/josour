"""Verified registration and password reset (ADR-0019). The caller delivers the returned mail.

``verify`` registration happens *before* there is an account. ``POST /auth/register`` stores a
:class:`~app.models.PendingSignup` and mails a six-digit code; the code proves the address; when
the server requires a phone, a second code by SMS proves that too; only then is the ``users`` row
written, active at once. Until that moment the address has no account, so whoever registers an
address that is already taken sees exactly what they would see for a free one - a code they never
receive - and the real owner gets an email saying somebody tried.

Codes are six random digits, stored as an HMAC keyed with the server's JWT secret, and die after
:data:`MAX_ATTEMPTS` wrong tries or :data:`CODE_TTL`. Asking for another one is allowed every
:data:`RESEND_COOLDOWN`, at most :data:`MAX_SENDS` times per registration. Wrong codes are
committed before the error is raised, so the attempt counter cannot be reset by an error path.
"""

import enum
import hashlib
import hmac
import re
import secrets
from dataclasses import dataclass
from datetime import datetime, timedelta

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.errors import ApiError, ValidationFailed
from app.core.security import (
    generate_opaque_secret,
    hash_opaque_secret,
    hash_password_async,
)
from app.models import PasswordReset, PendingSignup, SecurityEvent, User
from app.models.enums import (
    ApprovalStatus,
    RegisteredVia,
    RegistrationMode,
    SecurityEventType,
)
from app.services import admin_web_sessions, outbox
from app.services.security_events import record_event
from app.services.tokens import revoke_user_tokens

CODE_TTL = timedelta(minutes=15)
SIGNUP_TTL = timedelta(hours=1)
RESEND_COOLDOWN = timedelta(seconds=60)
MAX_ATTEMPTS = 5
MAX_SENDS = 5
DUPLICATE_NOTICE_INTERVAL = timedelta(hours=1)

_E164 = re.compile(r"\+[1-9]\d{7,14}")
_PHONE_PUNCTUATION = re.compile(r"[\s\-().]")


def invalid_code() -> ApiError:
    return ApiError(400, "invalid_code", "The code is wrong or has expired")


def new_code() -> str:
    return f"{secrets.randbelow(1_000_000):06d}"


def code_hash(secret: str, purpose: str, subject: str, code: str) -> str:
    message = f"{purpose}:{subject}:{code.strip()}".encode()
    return hmac.new(secret.encode(), message, hashlib.sha256).hexdigest()


def _matches(stored: str | None, secret: str, purpose: str, subject: str, code: str) -> bool:
    return stored is not None and hmac.compare_digest(
        stored, code_hash(secret, purpose, subject, code)
    )


def normalise_phone(value: str) -> str:
    """``+20 100 123-4567`` -> ``+201001234567``; anything that is not E.164 is refused."""
    phone = _PHONE_PUNCTUATION.sub("", value.strip())
    if phone.startswith("00"):
        phone = "+" + phone[2:]
    if not _E164.fullmatch(phone):
        raise ValidationFailed("phone must be an international number such as +201001234567")
    return phone


def _expired(moment: datetime | None) -> bool:
    value = ensure_utc(moment)
    return value is None or value <= utcnow()


def _plus(moment: datetime | None, delta: timedelta) -> datetime | None:
    value = ensure_utc(moment)
    return None if value is None else value + delta


def _cooling_down(sent_at: datetime | None) -> bool:
    value = ensure_utc(sent_at)
    return value is not None and utcnow() - value < RESEND_COOLDOWN


# ---------------------------------------------------------------- registration


class SignupStep(enum.StrEnum):
    PHONE_REQUIRED = "phone_required"
    ACTIVE = "active"


@dataclass(slots=True)
class SignupResult:
    step: SignupStep
    signup_token: str | None = None
    user: User | None = None


async def start_signup(
    db: AsyncSession,
    *,
    secret: str,
    email: str,
    password: str,
    display_name: str,
    ip: str | None,
) -> outbox.Outgoing:
    """``POST /auth/register`` in ``verify`` mode. Same answer, and the same argon2 work,
    whether or not the address has an account."""
    out = outbox.Outgoing()
    now = utcnow()
    await purge_expired_signups(db)
    password_hash = await hash_password_async(password)
    existing = await db.scalar(select(User).where(User.email == email))
    if existing is not None:
        recent = await db.scalar(
            select(SecurityEvent.id).where(
                SecurityEvent.type == SecurityEventType.REGISTRATION_DUPLICATE,
                SecurityEvent.user_id == existing.id,
                SecurityEvent.created_at > now - DUPLICATE_NOTICE_INTERVAL,
            )
        )
        record_event(
            db,
            SecurityEventType.REGISTRATION_DUPLICATE,
            user_id=existing.id,
            ip=ip,
            details={"mode": RegistrationMode.VERIFY.value},
        )
        if recent is None and existing.deleted_at is None:
            out.emails.append(outbox.account_exists_email(existing.email))
        return out

    code = new_code()
    row = await db.scalar(select(PendingSignup).where(PendingSignup.email == email))
    if row is not None and not _expired(row.expires_at):
        # Registering again: new password and name; a new code unless one just went out or the
        # registration has had its share of them.
        row.password_hash = password_hash
        row.display_name = display_name
        row.email_verified_at = None
        row.token_hash = None
        row.phone = None
        row.phone_code_hash = None
        row.phone_code_sent_at = None
        row.phone_attempts = 0
        row.phone_sends = 0
        if _cooling_down(row.email_code_sent_at) or row.email_sends >= MAX_SENDS:
            return out
        row.email_code_hash = code_hash(secret, "signup", email, code)
        row.email_code_sent_at = now
        row.email_attempts = 0
        row.email_sends += 1
    else:
        if row is not None:
            await db.delete(row)
            await db.flush()
        db.add(
            PendingSignup(
                email=email,
                password_hash=password_hash,
                display_name=display_name,
                created_at=now,
                expires_at=now + SIGNUP_TTL,
                email_code_hash=code_hash(secret, "signup", email, code),
                email_code_sent_at=now,
                email_attempts=0,
                email_sends=1,
            )
        )
    await db.flush()
    out.emails.append(outbox.signup_code_email(email, code, int(CODE_TTL.total_seconds() // 60)))
    return out


async def resend_signup_email(db: AsyncSession, *, secret: str, email: str) -> outbox.Outgoing:
    """Another email code for a registration still waiting for one. Silent otherwise."""
    out = outbox.Outgoing()
    row = await db.scalar(select(PendingSignup).where(PendingSignup.email == email))
    if (
        row is None
        or _expired(row.expires_at)
        or row.email_verified_at is not None
        or _cooling_down(row.email_code_sent_at)
        or row.email_sends >= MAX_SENDS
    ):
        return out
    code = new_code()
    row.email_code_hash = code_hash(secret, "signup", email, code)
    row.email_code_sent_at = utcnow()
    row.email_attempts = 0
    row.email_sends += 1
    await db.flush()
    out.emails.append(outbox.signup_code_email(email, code, int(CODE_TTL.total_seconds() // 60)))
    return out


async def confirm_signup_email(
    db: AsyncSession,
    *,
    secret: str,
    email: str,
    code: str,
    require_phone: bool,
    ip: str | None,
) -> SignupResult:
    row = await db.scalar(select(PendingSignup).where(PendingSignup.email == email))
    if (
        row is None
        or _expired(row.expires_at)
        or row.email_verified_at is not None
        or row.email_attempts >= MAX_ATTEMPTS
        or _expired(_plus(row.email_code_sent_at, CODE_TTL))
    ):
        raise invalid_code()
    if not _matches(row.email_code_hash, secret, "signup", email, code):
        row.email_attempts += 1
        await db.commit()
        raise invalid_code()
    row.email_verified_at = utcnow()
    if require_phone:
        token = generate_opaque_secret()
        row.token_hash = hash_opaque_secret(token)
        await db.flush()
        return SignupResult(step=SignupStep.PHONE_REQUIRED, signup_token=token)
    user = await _create_account(db, row, ip=ip)
    return SignupResult(step=SignupStep.ACTIVE, user=user)


async def _signup_by_token(db: AsyncSession, token: str) -> PendingSignup:
    row = await db.scalar(
        select(PendingSignup).where(PendingSignup.token_hash == hash_opaque_secret(token))
    )
    if row is None or _expired(row.expires_at) or row.email_verified_at is None:
        raise ApiError(400, "signup_expired", "Start the registration again")
    return row


async def start_signup_phone(
    db: AsyncSession, *, secret: str, token: str, phone: str
) -> outbox.Outgoing:
    """Send (or resend) the SMS code. Unlike the email step this one may say no plainly: the
    caller has already proved an address."""
    row = await _signup_by_token(db, token)
    phone = normalise_phone(phone)
    if await db.scalar(select(User.id).where(User.phone == phone)) is not None:
        raise ApiError(409, "phone_in_use", "This number already belongs to an account")
    if row.phone == phone and _cooling_down(row.phone_code_sent_at):
        raise ApiError(
            429,
            "rate_limited",
            "Wait a minute before asking for another code",
            headers={"Retry-After": str(int(RESEND_COOLDOWN.total_seconds()))},
        )
    if row.phone_sends >= MAX_SENDS:
        raise ApiError(429, "too_many_codes", "Start the registration again")
    code = new_code()
    row.phone = phone
    row.phone_code_hash = code_hash(secret, "phone", phone, code)
    row.phone_code_sent_at = utcnow()
    row.phone_attempts = 0
    row.phone_sends += 1
    await db.flush()
    return outbox.Outgoing(sms=[outbox.phone_code_sms(phone, code)])


async def confirm_signup_phone(
    db: AsyncSession, *, secret: str, token: str, code: str, ip: str | None
) -> SignupResult:
    row = await _signup_by_token(db, token)
    if (
        row.phone is None
        or row.phone_attempts >= MAX_ATTEMPTS
        or _expired(_plus(row.phone_code_sent_at, CODE_TTL))
    ):
        raise invalid_code()
    if not _matches(row.phone_code_hash, secret, "phone", row.phone, code):
        row.phone_attempts += 1
        await db.commit()
        raise invalid_code()
    user = await _create_account(db, row, ip=ip, phone=row.phone)
    return SignupResult(step=SignupStep.ACTIVE, user=user)


async def _create_account(
    db: AsyncSession, row: PendingSignup, *, ip: str | None, phone: str | None = None
) -> User:
    from app.services import contacts as contact_service
    from app.services.users import create_user

    email = row.email
    taken = await db.scalar(select(User.id).where(User.email == email))
    if taken is not None or (
        phone is not None and await db.scalar(select(User.id).where(User.phone == phone))
    ):
        # Somebody made the account (or claimed the number) while this was in flight.
        await db.delete(row)
        await db.commit()
        if taken is not None:
            raise invalid_code()
        raise ApiError(409, "phone_in_use", "This number already belongs to an account")
    now = utcnow()
    user = await create_user(
        db,
        email=email,
        password="",
        password_hash=row.password_hash,
        display_name=row.display_name,
        approval_status=ApprovalStatus.APPROVED,
        registered_via=RegisteredVia.SELF,
        email_verified_at=row.email_verified_at or now,
        phone=phone,
        phone_verified_at=now if phone else None,
    )
    await db.delete(row)
    await contact_service.attach_pending_invites(db, user)
    details = {"mode": RegistrationMode.VERIFY.value}
    record_event(
        db, SecurityEventType.REGISTRATION_SUBMITTED, user_id=user.id, ip=ip, details=details
    )
    record_event(
        db,
        SecurityEventType.SIGNUP_VERIFIED,
        user_id=user.id,
        ip=ip,
        details={"phone": phone is not None},
    )
    await db.flush()
    return user


async def purge_expired_signups(db: AsyncSession) -> int:
    result = await db.execute(delete(PendingSignup).where(PendingSignup.expires_at <= utcnow()))
    return result.rowcount or 0


# ---------------------------------------------------------------- password reset


async def request_password_reset(
    db: AsyncSession, *, secret: str, email: str, ip: str | None
) -> outbox.Outgoing:
    """``POST /auth/password/forgot``: the same silence whether or not the address has an
    account that may sign in."""
    out = outbox.Outgoing()
    user = await db.scalar(select(User).where(User.email == email))
    if user is None or not user.is_active or user.deleted_at is not None:
        return out
    row = await db.scalar(select(PasswordReset).where(PasswordReset.user_id == user.id))
    if row is not None and _cooling_down(row.created_at):
        return out
    code = new_code()
    now = utcnow()
    if row is None:
        row = PasswordReset(user_id=user.id)
        db.add(row)
    row.code_hash = code_hash(secret, "reset", user.email, code)
    row.attempts = 0
    row.created_at = now
    row.expires_at = now + CODE_TTL
    record_event(db, SecurityEventType.PASSWORD_RESET_REQUESTED, user_id=user.id, ip=ip)
    await db.flush()
    out.emails.append(
        outbox.password_reset_email(user.email, code, int(CODE_TTL.total_seconds() // 60))
    )
    return out


async def reset_password(
    db: AsyncSession, *, secret: str, email: str, code: str, password: str, ip: str | None
) -> None:
    """Set the new password, sign every device out (all refresh tokens and dashboard sessions
    are revoked) and lift a lockout - the person has just proved they own the address."""
    from app.services.users import validate_password

    validate_password(password)
    user = await db.scalar(select(User).where(User.email == email))
    row = (
        await db.scalar(select(PasswordReset).where(PasswordReset.user_id == user.id))
        if user is not None
        else None
    )
    if (
        user is None
        or row is None
        or not user.is_active
        or _expired(row.expires_at)
        or row.attempts >= MAX_ATTEMPTS
    ):
        raise invalid_code()
    if not _matches(row.code_hash, secret, "reset", user.email, code):
        row.attempts += 1
        await db.commit()
        raise invalid_code()
    user.password_hash = await hash_password_async(password)
    user.failed_logins = 0
    user.locked_until = None
    await db.delete(row)
    await revoke_user_tokens(db, user.id)
    await admin_web_sessions.revoke_all_for_user(db, user.id)
    record_event(db, SecurityEventType.PASSWORD_RESET, user_id=user.id, ip=ip)
    await db.flush()


def ensure_mail(senders: outbox.Senders) -> None:
    if not senders.mail_enabled:
        raise ApiError(503, "mail_unavailable", "This server does not send email")


def ensure_sms(senders: outbox.Senders) -> None:
    if not senders.sms_enabled:
        raise ApiError(503, "sms_unavailable", "This server does not send text messages")
