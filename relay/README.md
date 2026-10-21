# The relay service

It pairs the two ends of a session by its id and then pumps opaque bytes between them. The server-side implementation
of [ADR-0009](../docs/decisions/0009-relay-default.md); the client side is in
`client/src/Josour.Tunnel/Transport/`.

## What the service does not do — and that is the heart of its design

It does not terminate TLS, keep session state, read a database, or understand a single byte of what it passes through.
The TLS handshake between the two machines, the certificate pinning and `AUTH1`/`AUTH2` all run **inside** this stream
(`docs/protocol.md` sections 3 and 4). Whoever runs the relay — or whoever compromises it — sees ciphertext and traffic
sizes only. That is what makes the product's claim after ADR-0009, "**the server cannot read browsing data**", true and
provable.

## The wire contract

The binding source is `client/src/Josour.Tunnel/Transport/RelayProtocol.cs`, with `relay/protocol.py` as its other
half:

```
The preamble (client → relay): a fixed 24 bytes + the token
  4B  magic = "RBRL"
  u8  version = 1
  u8  role (0 = guest, 1 = host)
  16B session_id (UUID bytes, big-endian)
  u16 token_length (1..1024)
  NB  token

The reply (relay → client): two bytes
  u8  version = 1
  u8  status
```

| Status | Number | When |
|---|---|---|
| `paired` | 0 | Both sides arrived with different roles; everything after this is opaque bytes |
| `unauthorized` | 1 | The token's signature, or its session, or its role does not match |
| `unknown_session` | 2 | **Deliberately unused**: the service is stateless, so a valid token identifies its own session |
| `no_peer` | 3 | The other side did not arrive within `PAIR_TIMEOUT_SECONDS` |
| `busy` | 4 | The same role is already registered for this session; the first keeps its place |
| `protocol_error` | 5 | A malformed preamble, or silence until the timeout |
| `internal` | 6 | The session table or the per-address limit filled up, or an unexpected failure |

## The token

The service has no database: it does not know which sessions exist. The backend server does, so it says so in a signed
token (HS256) with the claims `sid`, `role` and `exp`, and the relay verifies the signature only. That keeps it
stateless and cheap, and it means compromising it **does not grant** the ability to issue access to a session — only to
verify it.

`RELAY_SECRET` is **deliberately separate from `JWT_SECRET`**: the second signs users' access tokens, and a copy of it
on a service exposed to the internet would mean compromising it becomes compromising sign-in.

And the token is bound to the role rather than to the session alone: without that, the holder of one token could open
both ends and pair with themselves, seizing the session and depriving the real party of it.

## Running it locally

```bash
cd relay
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"
pytest -q
ruff check . && ruff format --check .
RELAY_SECRET="$(openssl rand -hex 32)" RELAY_PORT=8443 python -m relay
```

## Deployment

Its own Compose stack, `deploy/docker-compose.relay.yml`, with its own settings file and project name — on the
backend server's machine or, better, on a host of its own in the Gulf
([ADR-0009](../docs/decisions/0009-relay-default.md) item 4): the relay sits on the path between the two sides, so
Egypt ↔ Saudi Arabia via Jeddah is about 40 milliseconds and via Europe about 150.

```bash
cd deploy
cp .env.relay.example .env.relay      # RELAY_SECRET: the same value as in the backend's deploy/.env
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
```

Use all three flags on every command (`ps`, `logs`, `down`, …). `.env.relay`, not `.env`: on the backend's machine
`deploy/.env` is the API's configuration, and copying the relay's example over it would wipe the database password
and `JWT_SECRET`. `-p josour-relay`: both compose files live in `deploy/`, so without it the two stacks would share
the project name `deploy` and treat each other's containers as orphans.

The container listens on 8443 as a non-root user (which cannot bind below 1024) and is published on the host as
`RELAY_PUBLIC_PORT`: 443 on a dedicated host, 8443 beside the backend (whose Caddy holds 443). The backend then
announces it with `RELAY_HOST`, `RELAY_PORT` and the shared `RELAY_SECRET` in `deploy/.env`. Docker publishes the port
past ufw, so `ufw allow` is not what opens it; a cloud provider's firewall does apply. **Nothing terminates TLS in
front of it**: the bytes are already encrypted between the two machines, so an intermediary adds a hop and reads
nothing. The full walk-through, including checking the port from outside: [docs/self-hosting.md part
3](../docs/self-hosting.md#3-the-relay--what-makes-a-session-work-with-no-router-configuration).

## The limits

| Setting | Default | What it protects |
|---|---|---|
| `PREAMBLE_TIMEOUT_SECONDS` | 5 | A scanner that connects and says nothing holds a slot |
| `PAIR_TIMEOUT_SECONDS` | 30 | Calibrated against the backend server's connect timeout |
| `IDLE_TIMEOUT_SECONDS` | 120 | The tunnel beats every 20 s, so a live session never approaches it |
| `MAX_SESSION_SECONDS` | 7200 | A hard ceiling above `max_session_minutes` (120) |
| `MAX_SESSIONS` | 64 | The table of concurrent sessions |
| `MAX_CONNECTIONS_PER_IP` | 16 | One source cannot exhaust the table |
| `MAX_BYTES_PER_SESSION` | 0 (no limit) | Cost control |

The numbers are sized for five users ([ADR-0009](../docs/decisions/0009-relay-default.md)). They are raised **by
measurement**, not by estimate.

## Performance

Measured in [docs/performance-relay.md](../docs/performance-relay.md): throughput around 2 Gbit/s for one session,
added latency below a millisecond, pairing at 0.6 ms, and fairness between sessions within 0.2%. Run it with
`python -m bench`.

## Status

The service is **wired end to end**: the backend server issues a token per side when the session is created and sends
it in `session.created.relay`, and the client builds a `RelayTransport` from it and races it against the direct path.

What remains before full adoption: running the benchmark on the deployment host to close the CPU-consumption
reservation ([docs/performance-relay.md](../docs/performance-relay.md) section 7).
