"""The relay half of ``session.created`` (ADR-0009).

The failure this file guards against is quiet: a deployment where the relay is half-configured,
or where a token is minted for the wrong role, produces sessions that simply do not connect and a
log that says nothing useful. So the configuration is checked at boot, and the tokens are checked
against the relay's own verifier rather than against a copy of the minting logic.
"""

import sys
import uuid
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.core.config import Settings
from app.models.enums import SessionRole
from app.services import relay_tokens
from tests.conftest import make_settings
from tests.ws_client import ASGIWebSocket

# The relay service is a separate package; importing its verifier is the point of these tests -
# checking a token against our own mint would only prove the mint agrees with itself.
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "relay"))

relay_protocol = pytest.importorskip(
    "relay.protocol", reason="the relay package is not installed in this environment"
)
relay_verify = pytest.importorskip("relay.tokens")

RELAY_SECRET = "relay-secret-that-is-at-least-32-bytes-long"


def relay_settings(**overrides) -> Settings:
    return make_settings(relay_host="relay.example.com", relay_secret=RELAY_SECRET, **overrides)


# ---------------------------------------------------------------- configuration


def test_relay_is_off_by_default() -> None:
    settings = make_settings()
    assert settings.relay_enabled is False
    assert settings.relay_host == ""


def test_relay_is_on_when_both_halves_are_present() -> None:
    settings = relay_settings()
    assert settings.relay_enabled is True
    assert settings.relay_port == 443


@pytest.mark.parametrize(
    "overrides",
    [
        {"relay_host": "relay.example.com"},
        {"relay_secret": RELAY_SECRET},
        {"relay_host": "relay.example.com", "relay_secret": "too-short"},
    ],
)
def test_half_a_relay_configuration_fails_at_boot(overrides: dict) -> None:
    """Silently falling back to direct-only is the one outcome ADR-0009 exists to prevent, so an
    incomplete configuration must stop the process rather than degrade the product."""
    with pytest.raises(ValidationError):
        make_settings(**overrides)


# ---------------------------------------------------------------- tokens


@pytest.mark.parametrize("role", [SessionRole.GUEST, SessionRole.HOST])
def test_minted_token_is_accepted_by_the_relay(role: SessionRole) -> None:
    session_id = uuid.uuid4()
    token = relay_tokens.mint(relay_settings(), session_id, role)
    relay_verify.verify(RELAY_SECRET, token, session_id, relay_protocol.Role(role.value))


def test_token_does_not_admit_the_other_role() -> None:
    session_id = uuid.uuid4()
    token = relay_tokens.mint(relay_settings(), session_id, SessionRole.GUEST)
    with pytest.raises(relay_verify.TokenError):
        relay_verify.verify(RELAY_SECRET, token, session_id, relay_protocol.Role.HOST)


def test_token_does_not_admit_another_session() -> None:
    token = relay_tokens.mint(relay_settings(), uuid.uuid4(), SessionRole.HOST)
    with pytest.raises(relay_verify.TokenError):
        relay_verify.verify(RELAY_SECRET, token, uuid.uuid4(), relay_protocol.Role.HOST)


def test_relay_secret_is_not_the_jwt_secret() -> None:
    """A relay is internet-facing by definition; signing logins with the same key would make a
    breach there a breach of authentication."""
    settings = relay_settings(jwt_secret="j" * 40)
    assert settings.relay_secret != settings.jwt_secret
    token = relay_tokens.mint(settings, uuid.uuid4(), SessionRole.HOST)
    with pytest.raises(relay_verify.TokenError):
        relay_verify.verify(settings.jwt_secret, token, uuid.uuid4(), relay_protocol.Role.HOST)


def test_token_fits_the_wire_limit() -> None:
    """The preamble caps the token at 1024 bytes; a token that outgrew it would fail at connect
    time on a real session and nowhere else."""
    token = relay_tokens.mint(relay_settings(), uuid.uuid4(), SessionRole.HOST)
    assert 0 < len(token.encode("utf-8")) <= relay_protocol.MAX_TOKEN_LENGTH


def test_minting_without_a_secret_is_refused() -> None:
    with pytest.raises(ValueError):
        relay_tokens.mint(make_settings(), uuid.uuid4(), SessionRole.HOST)


# ---------------------------------------------------------------- session.created


async def _open(app, actor, client=("203.0.113.10", 51000)) -> ASGIWebSocket:
    """``ws_connect`` is bound to the default app fixture; these tests need one built with relay
    settings, so the hello handshake is done here instead."""
    ws = ASGIWebSocket(app, client=client)
    await ws.open()
    await ws.send(
        {
            "type": "hello",
            "token": actor.token,
            "device_id": actor.device_id,
            "app_version": "0.1.0-test",
        }
    )
    await ws.expect("hello.ack", skip=frozenset())
    await ws.expect("hosts.snapshot", skip=frozenset())
    return ws


async def _created_frames(app, make_actor) -> dict[str, dict]:
    """Drive request -> accept over a real ``/ws`` pair and return each party's session.created."""
    guest = await make_actor("guest@example.com", display_name="Guest", device_name="GUEST-PC")
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    guest_ws = await _open(app, guest)
    host_ws = await _open(app, host, client=("198.51.100.9", 51000))
    try:
        await host_ws.send({"type": "host.available", "available": True})
        await guest_ws.expect("hosts.update", skip=frozenset())
        await guest_ws.drain(timeout=0.1)
        await host_ws.drain(timeout=0.1)

        await guest_ws.send(
            {
                "type": "request.create",
                "ref": "c1",
                "host_device_id": host.device_id,
                "duration_min": 30,
            }
        )
        await guest_ws.expect("request.created")
        incoming = await host_ws.expect("request.incoming")
        await host_ws.send(
            {"type": "request.accept", "ref": "h1", "request_id": incoming["request_id"]}
        )
        await guest_ws.expect("request.result")
        return {
            "guest": await guest_ws.expect("session.created"),
            "host": await host_ws.expect("session.created"),
        }
    finally:
        await guest_ws.disconnect()
        await host_ws.disconnect()


async def test_session_created_carries_a_usable_relay_token_per_party(
    app_factory, make_actor
) -> None:
    """The end-to-end check that matters: what the server actually puts on the wire is what the
    relay will actually accept, for that party and no other."""
    frames = await _created_frames(app_factory(relay_settings(relay_port=8443)), make_actor)

    for role, frame in frames.items():
        relay = frame["relay"]
        assert relay["address"] == "relay.example.com"
        assert relay["port"] == 8443
        relay_verify.verify(
            RELAY_SECRET,
            relay["token"],
            uuid.UUID(frame["session_id"]),
            relay_protocol.Role(role),
        )

    guest_token = frames["guest"]["relay"]["token"]
    assert guest_token != frames["host"]["relay"]["token"], "each party gets its own token"
    with pytest.raises(relay_verify.TokenError):
        relay_verify.verify(
            RELAY_SECRET,
            guest_token,
            uuid.UUID(frames["host"]["session_id"]),
            relay_protocol.Role.HOST,
        )


async def test_session_created_omits_relay_when_it_is_not_configured(
    app_factory, make_actor
) -> None:
    """No relay configured is a supported deployment: direct-only, exactly as before ADR-0009."""
    frames = await _created_frames(app_factory(None), make_actor)
    assert frames["guest"]["relay"] is None
    assert frames["host"]["relay"] is None
