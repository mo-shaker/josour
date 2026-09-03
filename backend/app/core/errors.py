"""Application error type rendered as the REST error envelope."""


class ApiError(Exception):
    status_code: int = 400
    code: str = "bad_request"

    def __init__(self, status_code: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message


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
