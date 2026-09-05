from pydantic import BaseModel, ConfigDict, Field


class DomainsOut(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {"version": 3, "entries": ["example.com", "=exact.com", "portal.corp:8443"]}
            ]
        }
    )

    version: int = Field(
        description="Immutable snapshot number, also sent as the `ETag`. `0` is the empty "
        "baseline before anything has been published."
    )
    entries: list[str] = Field(
        description="`example.com` matches subdomains too; a leading `=` means exact match; a "
        "trailing `:port` restricts the entry to that port."
    )
