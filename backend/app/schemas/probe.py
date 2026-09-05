import ipaddress

from pydantic import BaseModel, ConfigDict, Field, field_validator


class ProbeRequest(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"ip": "203.0.113.7", "port": 51820}]}
    )

    ip: str = Field(
        description="Public IPv4 or IPv6 address. Loopback, private, link-local, multicast and "
        "otherwise reserved addresses are refused with 400."
    )
    port: int = Field(ge=1, le=65535)

    @field_validator("ip")
    @classmethod
    def _valid_ip(cls, value: str) -> str:
        try:
            return str(ipaddress.ip_address(value.strip()))
        except ValueError as exc:
            raise ValueError("must be an IPv4 or IPv6 address") from exc


class ProbeResponse(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {"reachable": True, "latency_ms": 42},
                {"reachable": False, "latency_ms": None},
            ]
        }
    )

    reachable: bool
    """Whether the TCP handshake completed within the 3 second timeout."""
    latency_ms: int | None = Field(
        default=None, description="Handshake time in milliseconds; null when unreachable."
    )
