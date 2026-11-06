"""Outgoing email and SMS (ADR-0019): verification codes, password resets, invitations.

Services never send anything themselves. They return :class:`Email` and :class:`Sms` values;
the caller commits and then hands them to :func:`deliver`, exactly as :mod:`app.services.effects`
does for WebSocket follow-ups - a code must never reach somebody's phone for a row that was
rolled back. Delivery failures are logged, never raised: every endpoint that sends answers the
same whether or not a message went out, and the person can always ask for another code.

The senders live on ``app.state`` (built from :class:`~app.core.config.Settings` by
:func:`build_senders`), so a test can swap in :class:`MemoryOutbox` and read what was sent.
"""

import asyncio
import base64
import json
import logging
import smtplib
import ssl
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from email.message import EmailMessage
from typing import Protocol

from app.core.config import Settings

log = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class Email:
    to: str
    subject: str
    body: str


@dataclass(frozen=True, slots=True)
class Sms:
    to: str
    body: str


@dataclass(slots=True)
class Outgoing:
    emails: list[Email] = field(default_factory=list)
    sms: list[Sms] = field(default_factory=list)

    def __bool__(self) -> bool:
        return bool(self.emails or self.sms)


class MailSender(Protocol):
    async def send(self, message: Email) -> None: ...


class SmsSender(Protocol):
    async def send(self, message: Sms) -> None: ...


@dataclass(slots=True)
class Senders:
    mail: MailSender | None
    sms: SmsSender | None

    @property
    def mail_enabled(self) -> bool:
        return self.mail is not None

    @property
    def sms_enabled(self) -> bool:
        return self.sms is not None


# ---------------------------------------------------------------- backends


class ConsoleOutbox:
    """Development only (refused in ``prod``): the message, code included, goes to the log."""

    async def send(self, message: Email | Sms) -> None:
        kind = "email" if isinstance(message, Email) else "sms"
        log.warning("console %s to %s:\n%s", kind, message.to, message.body)


class MemoryOutbox:
    """Tests: keeps everything it was asked to send."""

    def __init__(self) -> None:
        self.sent: list[Email | Sms] = []

    async def send(self, message: Email | Sms) -> None:
        self.sent.append(message)

    def last_to(self, to: str) -> Email | Sms | None:
        for message in reversed(self.sent):
            if message.to == to:
                return message
        return None


class SmtpMailer:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings

    async def send(self, message: Email) -> None:
        await asyncio.to_thread(self._send_blocking, message)

    def _send_blocking(self, message: Email) -> None:
        s = self._settings
        mail = EmailMessage()
        mail["From"] = s.mail_from
        mail["To"] = message.to
        mail["Subject"] = message.subject
        mail.set_content(message.body)
        context = ssl.create_default_context()
        if s.smtp_security == "tls":
            server: smtplib.SMTP = smtplib.SMTP_SSL(
                s.smtp_host, s.smtp_port, timeout=20, context=context
            )
        else:
            server = smtplib.SMTP(s.smtp_host, s.smtp_port, timeout=20)
        with server:
            if s.smtp_security == "starttls":
                server.starttls(context=context)
            if s.smtp_username:
                server.login(s.smtp_username, s.smtp_password)
            server.send_message(mail)


class TwilioSms:
    """The Messages API over plain HTTPS (no SDK, no extra dependency)."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings

    async def send(self, message: Sms) -> None:
        await asyncio.to_thread(self._send_blocking, message)

    def _send_blocking(self, message: Sms) -> None:
        s = self._settings
        sender_key = "MessagingServiceSid" if s.twilio_from.startswith("MG") else "From"
        data = urllib.parse.urlencode(
            {"To": message.to, sender_key: s.twilio_from, "Body": message.body}
        ).encode()
        auth = base64.b64encode(f"{s.twilio_account_sid}:{s.twilio_auth_token}".encode()).decode()
        request = urllib.request.Request(
            f"https://api.twilio.com/2010-04-01/Accounts/{s.twilio_account_sid}/Messages.json",
            data=data,
            headers={"Authorization": f"Basic {auth}"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=20) as response:  # noqa: S310
                response.read()
        except urllib.error.HTTPError as exc:
            detail = exc.read()[:300].decode("utf-8", "replace")
            try:
                detail = json.loads(detail).get("message", detail)
            except ValueError:
                pass
            raise RuntimeError(f"twilio answered {exc.code}: {detail}") from exc


def build_senders(settings: Settings) -> Senders:
    mail: MailSender | None = None
    sms: SmsSender | None = None
    if settings.mail_backend == "console":
        mail = ConsoleOutbox()
    elif settings.mail_backend == "smtp":
        mail = SmtpMailer(settings)
    if settings.sms_backend == "console":
        sms = ConsoleOutbox()
    elif settings.sms_backend == "twilio":
        sms = TwilioSms(settings)
    return Senders(mail=mail, sms=sms)


async def deliver(senders: Senders, outgoing: Outgoing) -> None:
    """After the commit. Never raises: a failed send is logged and the person asks again."""
    for email in outgoing.emails:
        if senders.mail is None:
            continue
        try:
            await senders.mail.send(email)
        except Exception:
            log.exception("email delivery failed", extra={"subject": email.subject})
    for sms in outgoing.sms:
        if senders.sms is None:
            continue
        try:
            await senders.sms.send(sms)
        except Exception:
            log.exception("sms delivery failed")


# ---------------------------------------------------------------- the messages themselves
#
# Plain text, Arabic first and English after, like the rest of the product's copy. Codes are on
# a line of their own so a phone offers to copy them.


def signup_code_email(to: str, code: str, minutes: int) -> Email:
    return Email(
        to=to,
        subject=f"رمز التحقق في جسور / Josour verification code: {code}",
        body=(
            f"رمز التحقق الخاص بك في جسور:\n\n{code}\n\n"
            f"ينتهي خلال {minutes} دقيقة. إن لم تطلب إنشاء حساب فتجاهل هذه الرسالة.\n\n"
            "---\n\n"
            f"Your Josour verification code:\n\n{code}\n\n"
            f"It expires in {minutes} minutes. If you did not try to create an account, "
            "ignore this message.\n"
        ),
    )


def account_exists_email(to: str) -> Email:
    """Sent instead of a code when somebody registers an address that already has an account:
    the owner learns about it, the person registering learns nothing."""
    return Email(
        to=to,
        subject="حسابك في جسور / Your Josour account",
        body=(
            "حاول أحدهم إنشاء حساب جديد في جسور بعنوان بريدك، لكن لديك حساباً بالفعل. "
            "إن كنت أنت، فسجّل الدخول، أو استخدم «نسيت كلمة المرور». وإن لم تكن أنت فلا "
            "داعي لأي إجراء.\n\n---\n\n"
            "Someone tried to create a new Josour account with your email address, but you "
            'already have one. If it was you, sign in, or use "Forgot password". If it was '
            "not you, there is nothing to do.\n"
        ),
    )


def phone_code_sms(to: str, code: str) -> Sms:
    return Sms(to=to, body=f"Josour: {code} - رمز التحقق / verification code")


def password_reset_email(to: str, code: str, minutes: int) -> Email:
    return Email(
        to=to,
        subject=f"إعادة تعيين كلمة المرور في جسور / Josour password reset: {code}",
        body=(
            f"رمز إعادة تعيين كلمة المرور في جسور:\n\n{code}\n\n"
            f"ينتهي خلال {minutes} دقيقة. إن لم تطلبه فتجاهل هذه الرسالة؛ كلمة مرورك لم "
            "تتغير.\n\n---\n\n"
            f"Your Josour password reset code:\n\n{code}\n\n"
            f"It expires in {minutes} minutes. If you did not ask for it, ignore this "
            "message; your password has not changed.\n"
        ),
    )


def workspace_invite_email(to: str, inviter: str, workspace: str) -> Email:
    return Email(
        to=to,
        subject=f"دعوة للانضمام إلى {workspace} في جسور / Join {workspace} on Josour",
        body=(
            f"دعاك {inviter} للانضمام إلى مساحة «{workspace}» في جسور. افتح التطبيق وسجّل "
            "الدخول (أو أنشئ حساباً بهذا البريد) لتجد الدعوة.\n\n---\n\n"
            f'{inviter} invited you to join the "{workspace}" workspace on Josour. Open the '
            "app and sign in (or create an account with this email) to find the invitation.\n"
        ),
    )
