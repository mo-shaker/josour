"""The generated OpenAPI document is the executable REST contract (docs/api.md says so, and in
production ``/docs`` is admin-gated rather than absent). These tests fail the build when a route
is added or changed without carrying its documentation, so the schema cannot rot."""

from typing import Any

from fastapi import FastAPI
from fastapi.routing import APIRoute

MIN_DESCRIPTION_CHARS = 40

# Operations that answer without authentication and therefore have nothing to say about 401.
PUBLIC_OPERATIONS = {("/api/v1/auth/login", "post"), ("/api/v1/auth/logout", "post")}


def documented_routes(app: FastAPI) -> list[APIRoute]:
    return [r for r in app.routes if isinstance(r, APIRoute) and r.include_in_schema]


def test_every_route_has_an_explicit_summary(app: FastAPI) -> None:
    """``summary`` left unset makes FastAPI invent one from the function name ("Get Me"), which
    reads like documentation without being any. ``route.summary`` is None in that case, so this
    asserts the real thing was written."""
    missing = [f"{r.methods} {r.path}" for r in documented_routes(app) if not r.summary]
    assert missing == [], f"routes without an explicit summary: {missing}"


def test_every_route_has_a_description(app: FastAPI) -> None:
    """The description comes from the handler's docstring: what the endpoint does, and anything
    a caller cannot guess from the shapes."""
    short = [
        f"{r.methods} {r.path}"
        for r in documented_routes(app)
        if len((r.description or "").strip()) < MIN_DESCRIPTION_CHARS
    ]
    assert short == [], f"routes without a real description: {short}"


def test_every_route_documents_its_error_codes(app: FastAPI) -> None:
    schema = app.openapi()
    for path, operations in schema["paths"].items():
        for method, operation in operations.items():
            responses = operation["responses"]
            failures = [code for code in responses if code.startswith(("4", "5"))]
            assert failures, f"{method.upper()} {path} documents no failure response"
            if (path, method) not in PUBLIC_OPERATIONS:
                assert "401" in responses, f"{method.upper()} {path} does not document 401"


def test_error_responses_use_the_shared_envelope(app: FastAPI) -> None:
    """Every documented failure must render as ``{"error": {"code", "message"}}`` - including
    the 422 FastAPI would otherwise describe with its own ``HTTPValidationError`` shape, which
    this API never emits (app.api.errors rewrites it)."""
    schema = app.openapi()
    for path, operations in schema["paths"].items():
        for method, operation in operations.items():
            for code, response in operation["responses"].items():
                if not code.startswith(("4", "5")):
                    continue
                content = response.get("content", {})
                if not content:  # 304 and friends carry no body
                    continue
                ref = content["application/json"]["schema"].get("$ref", "")
                assert ref.endswith("/ErrorEnvelope"), (
                    f"{method.upper()} {path} -> {code} is documented as {ref or content}"
                )


def _resolve(schema: dict[str, Any], node: dict[str, Any]) -> dict[str, Any] | None:
    """Follow a ``$ref`` (or the item type of an array) to its component definition."""
    if node.get("type") == "array":
        node = node.get("items", {})
    ref = node.get("$ref")
    if not ref:
        return None
    return schema["components"]["schemas"][ref.rsplit("/", 1)[-1]]


def test_request_and_response_models_carry_examples(app: FastAPI) -> None:
    """Swagger renders an example body per model; a model without one leaves the reader to guess
    what a device secret, an allow-list entry or a diagnostics payload looks like."""
    schema = app.openapi()
    missing: list[str] = []
    for path, operations in schema["paths"].items():
        for method, operation in operations.items():
            bodies = []
            request_body = operation.get("requestBody")
            if request_body:
                bodies.append(request_body["content"]["application/json"]["schema"])
            for code, response in operation["responses"].items():
                if code.startswith("2") and response.get("content"):
                    bodies.append(response["content"]["application/json"]["schema"])
            for body in bodies:
                model = _resolve(schema, body)
                if model is not None and not model.get("examples"):
                    missing.append(f"{method.upper()} {path} -> {body}")
    assert missing == [], f"models without an example: {missing}"


def test_tags_are_grouped_and_described(app: FastAPI) -> None:
    schema = app.openapi()
    described = {tag["name"]: tag["description"] for tag in schema["tags"]}
    used = {
        tag
        for operations in schema["paths"].values()
        for operation in operations.values()
        for tag in operation.get("tags", [])
    }
    assert used, "no route is tagged"
    assert used <= set(described), f"tags without a description: {sorted(used - set(described))}"
    assert all(len(text) > MIN_DESCRIPTION_CHARS for text in described.values())


def test_api_description_documents_the_cross_cutting_rules(app: FastAPI) -> None:
    """The error envelope, the auth scheme and the rate limits are not visible per endpoint, so
    they must live in the document's own description."""
    description = app.openapi()["info"]["description"]
    for expected in ("Authorization: Bearer", "rate_limited", "Retry-After", "/ws"):
        assert expected in description, f"{expected!r} is not documented"
