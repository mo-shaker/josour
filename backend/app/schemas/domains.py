from pydantic import BaseModel


class DomainsOut(BaseModel):
    version: int
    entries: list[str]
