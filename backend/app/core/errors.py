"""Application error type rendered as the REST error envelope."""

from collections.abc import Mapping


class ApiError(Exception):
    status_code: int = 400
    code: str = "bad_request"

    def __init__(
        self,
        status_code: int,
        code: str,
        message: str,
        headers: Mapping[str, str] | None = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message
        self.headers: dict[str, str] | None = dict(headers) if headers else None


class Unauthorized(ApiError):
    def __init__(self, message: str = "Authentication required") -> None:
        super().__init__(401, "unauthorized", message)


class Forbidden(ApiError):
    def __init__(self, message: str = "Not allowed") -> None:
        super().__init__(403, "forbidden", message)


class NotFound(ApiError):
    def __init__(self, message: str = "Not found") -> None:
        super().__init__(404, "not_found", message)


class Conflict(ApiError):
    def __init__(self, message: str = "Conflict") -> None:
        super().__init__(409, "conflict", message)


class ValidationFailed(ApiError):
    def __init__(self, message: str, status_code: int = 422) -> None:
        super().__init__(status_code, "validation_error", message)


class RateLimited(ApiError):
    """429 with a ``Retry-After`` header (seconds) per docs/api.md."""

    def __init__(self, retry_after_seconds: int, message: str = "Too many requests") -> None:
        super().__init__(
            429, "rate_limited", message, headers={"Retry-After": str(retry_after_seconds)}
        )
        self.retry_after_seconds = retry_after_seconds
