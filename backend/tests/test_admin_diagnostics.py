from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectDiagnostic, User
from app.models.enums import SessionStatus
from app.services.users import create_user
from tests.conftest import DeviceFactory, SessionFactory

DIAGNOSTICS = "/api/v1/admin/diagnostics"
EMPTY = {
    "total_sessions": 0,
    "sessions_with_connect_result": 0,
    "connect_ok_ratio": 0.0,
    "winner_type_distribution": {},
    "tls_version_distribution": {},
    "connect_diagnostics_count": 0,
}


async def test_diagnostics_summary_empty(client: AsyncClient, admin_headers: dict) -> None:
    response = await client.get(DIAGNOSTICS, headers=admin_headers)
    assert response.status_code == 200
    assert response.json() == EMPTY


async def test_diagnostics_summary_with_data(
    client: AsyncClient,
    admin_headers: dict,
    db: AsyncSession,
    user: User,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    host = await create_user(db, email="host@example.com", password="pw-12345678", display_name="H")
    guest_device = await make_device(user, "G")
    host_device = await make_device(host, "H")
    await db.commit()

    common = dict(status=SessionStatus.ENDED, end_reason="guest_ended")
    await make_session(
        user,
        guest_device,
        host,
        host_device,
        connect_result="ok",
        winner_type="lan",
        tls_version="1.3",
        **common,
    )
    await make_session(
        user,
        guest_device,
        host,
        host_device,
        connect_result="ok",
        winner_type="upnp",
        tls_version="1.3",
        **common,
    )
    await make_session(
        user,
        guest_device,
        host,
        host_device,
        connect_result="ok",
        winner_type="lan",
        tls_version="1.2",
        **common,
    )
    await make_session(user, guest_device, host, host_device, connect_result="failed", **common)
    await make_session(user, guest_device, host, host_device, status=SessionStatus.CONNECTING)
    db.add(ConnectDiagnostic(device_id=guest_device.id, role="guest", data={"upnp_found": False}))
    db.add(
        ConnectDiagnostic(
            device_id=host_device.id, role="host", data={"firewall_rule_present": True}
        )
    )
    await db.commit()

    response = await client.get(DIAGNOSTICS, headers=admin_headers)
    assert response.status_code == 200
    assert response.json() == {
        "total_sessions": 5,
        "sessions_with_connect_result": 4,
        "connect_ok_ratio": 0.75,
        "winner_type_distribution": {"lan": 2, "upnp": 1},
        "tls_version_distribution": {"1.2": 1, "1.3": 2},
        "connect_diagnostics_count": 2,
    }
