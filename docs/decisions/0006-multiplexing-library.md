# ADR-0006: the multiplexing library for the tunnel

**Status:** accepted on 2026-09-04 after the week-two prototype (two days).

## Context
The tunnel between the two machines is one authenticated stream (`SslStream` from `SymmetricConnector`) that has to
carry hundreds of concurrent streams (a TCP connection for every `CONNECT` the browser makes) with per-stream
backpressure, a coded refusal to open a stream (`OPEN_FAIL`), and a clean half-close. The two candidates:
`Nerdbank.Streams.MultiplexingStream`, or hand-written framing following section 5 of `docs/protocol.md`.

## Decision
**`Nerdbank.Streams.MultiplexingStream` (version 2.13.31, protocol 3)** behind the `IMuxConnection` (guest side) and
`IMuxAcceptor` (host side) interfaces in `Josour.Tunnel.Mux`. The `NerdbankMux` class implements both and is created
from the authenticated stream with `NerdbankMux.Create(stream, role)`.

How the section-5 contract is met on top of Nerdbank:
- **OPEN:** the guest offers a channel named `host:port`. The host always accepts the channel and then writes **one
  status byte** as its first byte: `0` = `OPEN_OK` and pumping begins; otherwise the `OPEN_FAIL` code itself
  (1 `not_allowed` … 7 `ip_literal`), and the write completes. Refusing inside the channel itself guarantees
  ordering, needs no control channel per open, and does not rely on Nerdbank's own rejection semantics (which carry
  no reason).
- **PING/PONG/GOAWAY:** a seeded channel (id 0) that needs no handshake, with fixed 9-byte frames:
  `u8 type | 8 payload bytes`. `PING` every 20 seconds from both ends, and no `PONG` within 60 seconds means a dead
  tunnel (`Completion` fails with `MuxClosedException`). `GOAWAY(reason)` is sent before closing and appears at the
  other end as `RemoteGoAway`.
- **The window:** `DefaultChannelReceivingWindowSize = 1 MiB` per channel (the same as section 5); backpressure comes
  from `System.IO.Pipelines` — the writer stops when the other end's window is full.
- **Half-close:** `Output.Complete()` on the channel appears at the other end as EOF, and `StreamPump` turns that
  into `Shutdown(Send)` on the destination socket (`SocketStream`), and the reverse. The channel closes itself once
  both ends have finished writing.
- **The limits (256 streams, 50 OPEN/s)** are enforced in `Josour.Egress.StreamLimiter` and answer with
  `OPEN_FAIL(limit)`.
- **Tracing:** `MuxOptions.Trace` passes a `TraceSource` to Nerdbank to record frames when things break; `MuxStats`
  gives transferred bytes in both directions and the number of open streams.

## The prototype's numbers (macOS, TLS 1.2 over loopback, `tests/Josour.Tunnel.Tests/Mux/MuxBenchmarks.cs` tagged `Category=Benchmark`)

| Benchmark | Result |
|---|---|
| (a) a slow consumer on channel A, stalled for 3 seconds | Channel B carried **3005 MB in 3000 ms = 1002 MB/s** during the stall; A accepted only **1.0 MiB** before stopping (the window), and the consumer received nothing until the gate opened |
| (b) coded refusal to open | All seven reasons reach the guest as sent (`Open_Rejected_CarriesEncodedReason`) |
| (c) 100 MB on a single channel | Guest→Host **120 ms = 834 MB/s**; Host→Guest **116 ms = 861 MB/s**; 100,085,685 bytes on the wire for 100,000,000 bytes of payload (0.09% overhead) |
| (d) 256 concurrent channels × 1 MB each way | 256 channels opened in **26 ms**; the whole transfer in **688 ms = 744 MB/s** in total; the stream counter returns to 0 |
| (e) half-close | `CompleteWriting` on the guest → the socket's far end reads 0 (FIN) and can still reply afterwards; closing the far end → EOF at the guest; disposing at the guest closes the far socket |
| Extra: 1000 sequential open/close | **142 ms = 0.14 ms** each, with no leak |
| Extra: dead-tunnel detection | Swallow traffic silently → `MuxClosedException` within `DeadAfter` |

The acceptable floor was 30 MB in 3 seconds for benchmark (a) and 60 seconds for (c) and (d); the results are two
orders of magnitude above that, so the library is not the bottleneck in front of any realistic internet link.

### The week-five correction: these numbers were measured at RTT ≈ 0

**`docs/performance-week5.md` carries the same measurement over a simulated international RTT (50, 150 and 300 ms)
and bandwidth ceilings.** Its conclusion:

- **The conclusion above stays true but is not enough.** The library really is not the bottleneck: at 150 ms and
  300 ms throughput reached **99–102% of the theoretical ceiling**, the tunnel's overhead on a heavy page (a document
  plus 80 resources over 30 paths) stayed between 0% and 2% compared to a TCP connection per resource on the same
  link, and opening a stream costs **one RTT** — exactly the cost of a TCP handshake.
- **But a ceiling that was invisible over loopback became the governing one:** a single stream's throughput is
  **the window ÷ RTT**. With a 1 MiB window (this ADR and section 5 of `docs/protocol.md`) that means **56 Mbit/s at
  150 ms and 28 Mbit/s at 300 ms** per stream. The "1002 MB/s" in benchmark (a) and the "834 MB/s" in (c) say nothing
  about this, because RTT was zero.
- **The practical effect is bounded but real:** pages and video (5 Mbit/s sustained for 60 seconds with no stall and
  no memory growth) are unaffected, while **one large download** on a link of ≥ 100 Mbit/s across an intercontinental
  RTT is limited by the window rather than by the link.
- **Window isolation (benchmark a) held up at a real RTT:** 256 concurrent streams at 150 ms, 32 of them with a
  stalled consumer, each accepted **exactly 1.00 MiB** (one window) while the other 224 finished their transfer in
  1.7 RTT.

**The recommendation (for the owners of `docs/protocol.md` section 5):** derive the window from the RTT measured at
connect time — 1 MiB up to 60 ms, 2 MiB up to 150 ms, 4 MiB above that — so that a stream's ceiling stays above
100 Mbit/s, with a session-level memory budget because 256 × 4 MiB = 1 GiB as a theoretical ceiling. The measurement
shows 4 MiB gives 224 Mbit/s at 150 ms and 112 Mbit/s at 300 ms.

### What the contract settled on: the window is derived from the RTT, not a fixed 1 MiB

**The recommendation above was adopted.** Section 5 of `docs/protocol.md` was amended, so the window in this ADR is no
longer a fixed number:

- **The window:** `DefaultChannelReceivingWindowSize` = whatever `MuxWindow.ForRoundTrip(connect_ms)` returns in
  `Josour.Tunnel.Mux`: **1 MiB up to 60 ms, 2 MiB up to 150 ms, 4 MiB above that**. The caller is `TunnelSession`
  after `SymmetricConnector` — the first point where `connect_ms` is known. A corrupt measurement (zero, negative, or
  > 5 seconds) means the middle band with a written reason. `MuxOptions.ReceiveWindow` remains an explicit override
  that beats the derivation (the tests and the spike tool).
- **The limits:** the concurrency limit follows the window (**256 / 128 / 64**), so the product stays 256 MiB, and it
  reaches `Josour.Egress.StreamLimiter` through `TunnelEgressContext.MaxConcurrentStreams`. The 50 `OPEN`/second limit
  is unchanged. The guest also reserves the stream's slot locally before offering, against its own band's limit, so
  its memory ceiling stays 256 MiB even if the two ends land in different bands (the host enforces its limit on the
  wire; this protects the guest's own memory).
- **Nothing is negotiated on the wire:** in Nerdbank protocol 3 the receiving window is a property of the **receiver**
  and is announced per channel in the offer/accept frame (`localWindowSize` = what we announced, `remoteWindowSize` =
  the sending credit as the other end announced it), so each side derives its window from its own measurement and the
  two bands may differ harmlessly. Only the seeded channel (id 0) goes through no offer/accept, so its window is
  pinned at 4 MiB on both ends so it cannot differ by band.
- **The numbers after the change** (`docs/performance-week5.md` section 8, the `[derived]` benchmark): 162 Mbit/s at
  50 ms, **113 Mbit/s at 150 ms** (was 56), and **113 Mbit/s at 300 ms** (was 28), at 101–102% efficiency and a
  256 MiB memory ceiling in every band. There is also an important caveat: `connect_ms` measures the whole connection,
  not a single round trip.

## Reasons
- It meets all four decision criteria with no hand-written framing (some 600 lines plus fuzz tests) and no hand-managed
  windows.
- The dependency is small and known: `Nerdbank.Streams` 2.13.31 (MIT) + `Microsoft.VisualStudio.Threading.Only`
  17.13.61 + `Microsoft.VisualStudio.Validation` 17.8.8 + `System.IO.Pipelines` 8.0.0.
- What concerns security (the policy, the limits, DNS, blocking) stays in our own code (`Egress`), not in the library.

## Consequences
- Section 5 of `docs/protocol.md` stays the reference for the semantics (the window, the limits, PING/PONG, the
  OPEN_FAIL/GOAWAY reasons) and for the hand-written alternative if we ever need it; the frame format on the wire is
  Nerdbank v3's, not the hand-written 8-byte header.
- Guest and host both use `NerdbankMux.Create` on the same stream `SymmetricConnector` returns; track C sees nothing
  but `IMuxConnection`/`IMuxAcceptor`.
- Verification is still needed on Windows: the same measurement over Schannel on Win10 (TLS 1.2) and Win11 (TLS 1.3).
  "International RTT" was pulled forward into week five and measured on a simulated link in
  `docs/performance-week5.md`; verification on a real network between two machines is still outstanding.
