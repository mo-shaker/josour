# The channel contract between the two machines (the tunnel protocol)

Version: 1 (frozen at the end of week 1). Owner: track B. Consumer: track C through the `Josour.Core` interfaces.

## 1. The roles

- **Guest (the user):** runs `ConnectProxyServer` locally and opens streams through the tunnel. It always speaks first
  in the authentication.
- **Host:** runs `OpenHandler` and enforces `EgressPolicy`. It is the sole authority on accepting the connection
  (`session.connected`).
- Either side may be **the connector** or **the listener** at the TCP level (a symmetric connection). The logical role
  does not change.

## 2. Establishing the connection

1. On `session.created` each side:
   - Generates a self-signed certificate: ECDSA P-256, subject `CN=josour`, EKU `serverAuth` + `clientAuth`, validity
     from now − 5 minutes to `expires_at` + an hour. It is exported as PFX and re-imported with
     `X509KeyStorageFlags.UserKeySet` (without `PersistKeySet`) so that it works with Schannel and the key container is
     deleted on `Dispose`.
   - Opens a TCP listener on `[::]:0` with `DualMode=true` and reads the port.
   - Requests a UPnP/NAT-PMP mapping with Mono.Nat for that port (the mapping's lifetime = the remaining duration + 5
     minutes).
   - Gathers the candidates and sends `session.endpoint` with the certificate's fingerprint (SHA-256 over `RawData`,
     lowercase hex).
2. The candidate types and their priority order: `lan` (only if `same_public_ip=true`), `v6` (public, non-temporary
   IPv6 addresses), `upnp` (the external IP:Port from the router), `public` (the IP the server sees + the local port;
   it works if the port is open or the machine has a public address).
3. On `session.peer_endpoint` each side connects to all of the other's candidates in parallel with a 5-second timeout
   per candidate, and keeps accepting inbound connections.
4. For every TCP connection: a TLS handshake with a 10-second timeout, then authentication with a 5-second timeout.
5. The host keeps the **first** connection that passes `AUTH1`, sends `AUTH2` on that one **only**, and closes any
   other connection with no reply. So the user sees only one `AUTH2`, and its choice of "the first authenticated
   connection" is necessarily the host's choice. The host then sends `session.connected`. Both sides close the
   listener, remove the UPnP mapping and cancel the other attempts. In the diagnostics the two sides may label
   `winner_type` differently for the same connection; the host's label is authoritative.
6. If nothing authenticates within 30 seconds of `session.created`: `session.connect_failed` with the diagnostics.

The listener's limits: at most 4 pending unauthenticated connections; any beyond that is closed at once. The listener
is only open between `session.created` and `session.connected`.

## 3. TLS

- `SslStream` with `SslProtocols.None` (the system default). After the handshake: if `SslProtocol < Tls12` the
  connection is closed and `tls_too_old` recorded. The negotiated version is sent in `session.connected`.
- The connecting side (the TLS client) presents `TargetHost="josour"` and accepts the certificate only if
  `SHA256(RawData)` matches the other side's fingerprint from `session.peer_endpoint`; it ignores chain and name
  errors; `CertificateRevocationCheckMode=NoCheck`.
- The listening side (the TLS server) presents its self-signed certificate and does not request a client certificate.
- **Over the relay (added on 2026-09-07 by [ADR-0009](decisions/0009-relay-default.md)):** there is no listener at all
  — both sides connect outbound to the relay, so the rule "the listener is the server" is meaningless, and had it been
  left in place both would have waited for the other's handshake forever. The rule on this path: **the host is always
  the TLS server** (presenting its certificate), **and the user is the TLS client** (pinning the host's fingerprint).
  It is the choice consistent with the host being the authority on acceptance in section 2. Accordingly
  `listener_cert_fp` in section 4 is **the host's certificate fingerprint** on this path — known to both sides (the
  host from its certificate, the user from `session.peer_endpoint`), so not a single byte of the `AUTH1`/`AUTH2`
  computation changes.
- Not a single byte is written outside TLS.

## 4. Authentication inside TLS

After the handshake, the guest sends `AUTH1` and then waits for `AUTH2`. The host is silent until it has verified
`AUTH1`.

```
AUTH1 (Guest → Host), 81 bytes:
  u8   version = 1
  16B  session_id (UUID bytes, big-endian as in RFC 4122)
  32B  client_random
  32B  mac1 = HMAC-SHA256(secret, "rb-auth1" || session_id || client_random || listener_cert_fp)

AUTH2 (Host → Guest), 32 bytes:
  32B  mac2 = HMAC-SHA256(secret, "rb-auth2" || session_id || client_random || listener_cert_fp)
```

- `secret` = the 32 bytes from `secret_b64` in `session.created`.
- `listener_cert_fp` = the 32-byte SHA-256 of the certificate of **the side acting as the TLS server** in this
  connection (whatever its logical role). Both sides know it: the listener from its own certificate, the connector from
  `session.peer_endpoint`.
- The strings `"rb-auth1"` and `"rb-auth2"` are ASCII with no separator.
- Comparison uses `CryptographicOperations.FixedTimeEquals`.
- Failure: the connection is closed with no reply. On success the mux starts at once.
- When the session ends: `secret` is wiped from memory (`CryptographicOperations.ZeroMemory`) and the certificates are
  disposed.

## 5. The frames (used if Nerdbank is not adopted in ADR-0006)

A fixed 8-byte header, big-endian:

```
u8  type | u8 flags | u16 length | u32 stream_id
```

| type | Name | Payload | Notes |
|---|---|---|---|
| 0x01 | `OPEN` | `u16 port` + `u8 hostlen` + `host` (UTF-8, ASCII/Punycode) | From the guest only. `stream_id` is odd and increasing |
| 0x02 | `OPEN_OK` | nothing | |
| 0x03 | `OPEN_FAIL` | `u8 reason` | 1 `not_allowed`, 2 `private_ip`, 3 `port_not_allowed`, 4 `dns_failed`, 5 `connect_failed`, 6 `limit`, 7 `ip_literal` |
| 0x04 | `DATA` | bytes ≤ 16384 | |
| 0x05 | `WINDOW_UPDATE` | `u32 increment` | |
| 0x06 | `CLOSE` | nothing | A half-close (this side will send no more DATA) |
| 0x07 | `RST` | nothing | Immediate termination of the stream in both directions |
| 0x08 | `PING` | 8 opaque bytes | `stream_id = 0` |
| 0x09 | `PONG` | the same 8 bytes | `stream_id = 0` |
| 0x0A | `GOAWAY` | `u8 reason` | `stream_id = 0`; 1 `session_end`, 2 `protocol_error`, 3 `expired` |

Rules:
- `length` ≤ 16384 for every type; exceeding it = `GOAWAY(protocol_error)` and closing the connection.
- An unknown `stream_id` in `DATA`/`CLOSE`/`WINDOW_UPDATE` → `RST` for that id.
- **A per-stream receiving window derived from the round-trip time**, because a single stream's ceiling is exactly the
  window divided by the RTT (the measurements in `docs/performance-week5.md`: 99% to 102% efficiency):

| Measured RTT | Window | Theoretical ceiling for one stream |
|---|---|---|
| ≤ 60 ms | 1 MiB | ≥ 140 Mbit/s |
| ≤ 150 ms | 2 MiB | ≥ 110 Mbit/s |
| > 150 ms | 4 MiB | ≥ 110 Mbit/s up to 300 ms |

  The sender does not exceed its credit. The receiver returns credit **after** writing the bytes to the destination
  socket, and sends `WINDOW_UPDATE` when the consumed amount reaches 25% of the window.

  **The measurement's source (corrected in week 5):** one explicit round trip on the authenticated stream after `AUTH2`
  and before the mux is created. **`connect_ms` is not used**: it measures the whole connection (TCP, then TLS, then
  authentication, plus the candidate race and the gap between the two sides' start times) and so comes to about three
  or four times the real RTT, which raises the session a band or two and lowers the concurrent stream limit needlessly
  until a browser on a heavy page runs into it.

  The window is a **receiver's** property that each side announces about itself, so the two sides need not agree on one
  band; but each side guards its own memory budget (see the memory limit below). The seeded control channel (id 0)
  exchanges no offer and accept, so it is pinned at 4 MiB across every band.

  **Why the banding:** a fixed 1 MiB window limited a single download to 56 Mbit/s at an RTT of 150 ms and 28 Mbit/s at
  300 ms. Page loading is unaffected (it uses dozens of streams, and the increase over a direct connection is 0% to
  2%), and video is unaffected, but **downloading a single work file** is affected, and that is a use the product
  document treats as expected (section 13.1).

  **The memory limit:** the window is credit rather than a reservation, so actual consumption is governed by the
  bandwidth-delay product of the traffic in flight. The theoretical worst case (every stream stalled with its window
  full) is 256 × the window; at 4 MiB that is 1 GiB on what may be a personal computer, so **the maximum number of
  concurrent streams is lowered to 64 when the window is 4 MiB** (a 256 MiB ceiling) and to 128 at 2 MiB, and stays 256
  at 1 MiB.
- One writer per connection drains a bounded channel (32 frames); every stream has at most one DATA frame outstanding
  in the channel.
- The host's limits: the number of concurrent streams per the window table above (256, 128 or 64), and 50 `OPEN`s a
  second; exceeding them returns `OPEN_FAIL(limit)`.
- Liveness: `PING` every 20 seconds from both sides; no `PONG` within 60 seconds = the tunnel is dead = the session
  ends.

## 6. The egress policy on the host (`EgressPolicy`)

> **Why is an entry like `portal.corp:8443` accepted in the list?** (a question that recurred in week five's security
> review)
> The list is an **authorisation**, not a guarantee of reachability: it determines which names may be requested through
> the host. The actual security boundary is rule 6 below, and it is applied **after the name is resolved and before any
> socket is opened**, on both sides: the host in `EgressPolicy`, and the user on the direct path in
> `ConnectProxyServer`. So a name that is in the list but resolves to an internal address **is refused**. That is why
> such an entry is not rejected at load time: rejecting it would break the `api.md` contract with no security gain,
> while the protection stands in its proper place.

On `OPEN(host, port)`, in order:

1. If `host` is an IP address (v4 or v6, even in brackets) → `OPEN_FAIL(ip_literal)`.
2. Normalisation: lowercase, strip the trailing dot, convert IDN to Punycode.
3. Matching the list (`AllowlistMatcher`) — **lifted by default since
   [ADR-0010](decisions/0010-route-all-through-host.md)**: every name is allowed unless an administrator turns on
   `enforce_allowlist`. Nothing else in these steps changes: step 6 (refusing any resulting blocked address) is the
   security boundary and stays in force as it is, as do refusing address literals, strict normalisation and the
   `allowed_ports` limit. "Pass every site" lifts the name condition alone:
   - `example.com` matches `example.com` and every subdomain.
   - `=exact.com` matches `exact.com` only.
   - An optional `:port` suffix restricts the port; without it the `allowed_ports` ports are permitted.
   - Entries rejected at load time: empty, `*`, or containing `/` or spaces.
   - No match → `OPEN_FAIL(not_allowed)`.
4. The port is not within `allowed_ports` (80 and 443 by default) and does not match the entry's restriction →
   `OPEN_FAIL(port_not_allowed)`.
5. DNS resolution once (`Dns.GetHostAddressesAsync`) with a 5-second timeout; failure → `OPEN_FAIL(dns_failed)`.
6. If **any** resulting address is blocked (`IpRangePolicy`) → `OPEN_FAIL(private_ip)`. The blocked addresses:
   - IPv4: `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`,
     `192.0.0.0/24`, `192.0.2.0/24`, `192.168.0.0/16`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`,
     `224.0.0.0/4`, `240.0.0.0/4`, `255.255.255.255/32`.
   - IPv6: `::/128`, `::1/128`, `::ffff:0:0/96`, `64:ff9b::/96`, `2002::/16` and `2001::/32` (unwrapped, with the
     embedded v4 checked), `fc00::/7`, `fe80::/10`, `ff00::/8`.
   - Every address of the host's own interfaces, its default gateways, and its public address as the server sees it.
7. Connecting with `Socket.ConnectAsync(IPAddress[], port)` using the checked list only, with a 10-second timeout;
   failure → `OPEN_FAIL(connect_failed)`.
8. `OPEN_OK`, then bidirectional pumping; counting the bytes in both directions; adding `host` to the set of distinct
   domains.

## 7. The cleanup order when the session ends

1. The proxy stops accepting new connections.
2. A polite browser close (WM_CLOSE), then closing the Job Object after 3 seconds.
3. `GOAWAY(session_end)` and closing the tunnel.
4. Closing the listener and removing the UPnP mapping.
5. Disposing the certificates and wiping the secret.
6. `session.end` with the statistics and the domains.

## 8. The `Josour.Core` interfaces (what track C consumes)

```csharp
public interface ITunnelSession : IAsyncDisposable
{
    string SessionId { get; }
    TunnelRole Role { get; }
    TunnelState State { get; }
    event Action<TunnelState> StateChanged;
    Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, CancellationToken ct);
    TunnelStats Stats { get; }
    IReadOnlyCollection<string> DomainsSeen { get; }   // Host only
    Task EndAsync(TunnelEndReason reason);
}

public interface ITunnelTransport { Task<Stream> ConnectAsync(...); }   // Direct today, Relay later
```

The binding definition is in `client/src/Josour.Core/Tunnel/*.cs`.
