import ipaddress

from pydantic import BaseModel, Field, field_validator


class ProbeRequest(BaseModel):
    ip: str
    port: int = Field(ge=1, le=65535)

    @field_validator("ip")
    @classmethod
    def _valid_ip(cls, value: str) -> str:
        try:
            return str(ipaddress.ip_address(value.strip()))
        except ValueError as exc:
            raise ValueError("must be an IPv4 or IPv6 address") from exc


class ProbeResponse(BaseModel):
    reachable: bool
    latency_ms: int | None
