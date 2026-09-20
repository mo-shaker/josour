# Soak and fuzz — week 6 (track B)

Owner: track B. Scope: `client/src/Josour.{Core,Tunnel,Egress,Proxy,Browser}` and `client/tools/Josour.Spike`.

This week's question is one: **what is revealed only by time or by hostile input?** Everything before this was measured
in seconds, and the tunnel's frame parser — the most dangerous parser in the product — had never been exercised with
hostile input (week 4 exercised the HTTP parser and the site list, not the mux).

---

## 1. How it is run

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd client
```

### The soak

It all lives in `tests/Josour.Tunnel.Tests/Soak/`, and it is all tagged `Category=Benchmark` so it does not enter the
default suite. It runs unattended, and environment variables configure it rather than a recompile:

| Variable | Default | Meaning |
|---|---|---|
| `ROUTEBRIDGE_SOAK_MINUTES` | 30 | The duration. Exercised up to 32 minutes; 240 and beyond are supported by design |
| `ROUTEBRIDGE_SOAK_SAMPLE_S` | 15 | The interval between samples |
| `ROUTEBRIDGE_SOAK_RTT_MS` | 150 | The round-trip time on the link simulator (`docs/performance-week5.md`) |
| `ROUTEBRIDGE_SOAK_OUT` | — | The machine-readable output directory; without it the results are only printed into the test log |

```bash
# half an hour on a tunnel under load, and half an hour on an idle tunnel, in parallel in two processes
ROUTEBRIDGE_SOAK_MINUTES=30 ROUTEBRIDGE_SOAK_OUT=/tmp/soak \
  dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~WorkingTunnel_DoesNotGrowOverTime'
ROUTEBRIDGE_SOAK_MINUTES=30 ROUTEBRIDGE_SOAK_OUT=/tmp/soak \
  dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~IdleTunnel_AccumulatesNothing'
# the session's lifecycle (a certificate + a listener + candidates) — 5 minutes by default
ROUTEBRIDGE_SOAK_OUT=/tmp/soak \
  dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~SessionLifecycleSoakTests'
```

**The machine-readable output** for every run in `ROUTEBRIDGE_SOAK_OUT`:

- `<name>.jsonl` — one sample per line **during** the run (so nothing is lost if a four-hour run is cut off in its
  third hour). The fields: `ElapsedSeconds`, `ManagedHeapBytes`, `WorkingSetBytes`, `Gen0/1/2`, `ThreadCount`,
  `OpenHandles`, `GuestOpenStreams`, `HostOpenStreams`, `GuestPendingPings`, `HostPendingPings`, `StreamsOpened`,
  `StreamsReset`, `BytesMoved`, `LatencyP50Ms`, `LatencyP95Ms`, `LatencyMaxMs`.
- `<name>.summary.json` — a trend table per metric: the first half's median, the second half's median, the difference,
  the slope per hour, and the minimum and maximum.

**The verdict is a trend, not an instant.** The criterion "anything that grows monotonically is a leak" is applied by
comparing the second half's median with the first half's and by a linear-regression slope per hour, not by a single
peak. And managed memory is measured **after a forced full collection** at every sample, otherwise the graph would be
uncollected garbage rather than a leak. And the collections the probe itself forces (three full ones per sample) **are
subtracted** from the `Gen0/1/2` columns, otherwise the column would measure the probe rather than the application's
allocation pressure — and on an idle tunnel the probe's collections were all that appeared in it.

The probe itself has units in the default suite (`Soak/SoakSupportTests.cs`): a tool nobody measures rusts silently and
then reports "no leak" because it sees nothing. The units prove it sees growth when there is growth, sees zero when
there is none, and that the resource counter returns a number on this system at all.

### The fuzz

It all lives in `tests/Josour.Tunnel.Tests/Fuzz/`. **The fast cases run in the default suite** (72 tests in 5 seconds),
and the deep ones are tagged `Category=Benchmark`:

```bash
dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~Fuzz&Category!=Benchmark'   # the fast ones
dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~Fuzz&Category=Benchmark'    # the deep ones
ROUTEBRIDGE_FUZZ_CASES=20000 dotnet test tests/Josour.Tunnel.Tests --filter 'FullyQualifiedName~Fuzz'
```

**Every case is seeded.** `FuzzSeed.For(scope, i)` derives a generator from a fixed root seed, so any failure is
reproduced literally: the failure message carries the scope's name, the case number and the mutation's name.

**The corpus is recorded from a real session rather than guessed.** `MuxCorpus` runs an actual tunnel (a successful
open with data in both directions, a refusal with all seven reasons, a PING, a half-close, a GOAWAY) and records what
goes out on the wire — about 8.5 KB of real Nerdbank frames — and then mutates it with eight transformations:
completely random, bit flips, truncation, deleting a slice, repeating a slice, saturation with `0xFF` (so any length
field inside it becomes the largest it can be), reordering blocks, and truncation with bit flips. And every payload has
two modes: one batch, or split across 1..3-byte writes with a flush after every piece (a frame split across small
reads).

---

## 2. The boundary of responsibility in the fuzz

ADR-0006 delegates the frame format to `Nerdbank.Streams`, so the fuzz falls on the boundaries **we own**:

| The boundary | File | What is exercised |
|---|---|---|
| The authenticated stream handed to `NerdbankMux.Create` | `Fuzz/MuxTransportFuzzTests.cs` | What our process does when malformed frames arrive: the exception type, hanging, allocation |
| The seeded control channel (PING/PONG/GOAWAY, 9 bytes) | `Fuzz/MuxControlFuzzTests.cs` | Our own encoding, through a peer that speaks Nerdbank 3 perfectly and lies on top of it |
| The open protocol: the `host:port` name and the status byte | `Fuzz/MuxControlFuzzTests.cs` | What we own inside the channel |
| `AUTH1`/`AUTH2` | `Fuzz/AuthHandshakeFuzzTests.cs` | The first thing read from an unauthenticated peer |
| `RelayProtocol`'s preamble | `Fuzz/RelayProtocolFuzzTests.cs` | The first thing read on the relay path |

The invariants asserted in every case on the mux:

1. **No unexpected exception type** — `Completion` either finishes cleanly or faults with `MuxClosedException` alone.
2. **No hang** — every case finishes within an explicit timeout, and disposing the mux finishes too.
3. **No unbounded allocation** — managed memory stays under a ceiling despite declared lengths the size of
   `0xFFFFFFFF`.
4. **No unobserved task exception of our own making** — the assembly does not run in parallel, so what is caught is
   from this run.

---

## 3. What the fuzz found, and what was fixed

### F-1 — a protocol error on the control channel ended the tunnel as a "clean close" (medium, fixed)

`NerdbankMux.ProcessControlFrameAsync`. The other side sends a control frame with a type outside {PING, PONG, GOAWAY},
or a `GOAWAY` with a reason outside the contract, or an explicit `GOAWAY(protocol_error)`. In all three cases the path
was: `ShutdownAsync` → `Finish()` → **`Completion` completing successfully**. And `TunnelSession.WatchAsync` only
raises `Died` on a failure, so the result:

- no `Died` event, so the application does not know the tunnel died;
- and the server is told of a **normal** end reason for a session that ended in a protocol breach;
- and the branch `GoAwayReason.ProtocolError => TunnelEndReason.ProtocolError` in `SuggestDeathReason` was **dead
  code** that could not be reached at all — which is the clearest evidence that the behaviour was not intended.

This is of the same class as the two defects fixed in weeks 2 and 5 ("a dead tunnel appearing as a clean close"), and
this path of it had remained.

**The fix:** `FailProtocolAsync` sends `GOAWAY(protocol_error)` if we are the ones who found the error, and then
**faults** `Completion` with `MuxClosedException`. `GOAWAY(session_end)` and `GOAWAY(expired)` stay a clean close as
the contract states: a deliberate end, not a death. And the control loop now stops at the first terminating frame
rather than reading on past it.

**Its effect on track C:** nothing needs changing — `SessionCoordinator` already handles
`TunnelEndReason.ProtocolError`, and the interface has a translated string (`SessionEndedProtocolError`). The fix made
an already-supported path **reachable**.

The tests: `UnknownControlFrameType_SendsGoAwayProtocolError_AndFaultsCompletion`,
`GoAwayWithUnknownReason_IsAProtocolError`, `PeerGoAwayProtocolError_FaultsCompletion`,
`PeerGoAwayWithContractReason_StaysAClean_Close`.

### F-2 — the channel-acceptance path had no limit of its own (medium, fixed)

`NerdbankMux.HandleOfferAsync` incremented the counter and created a channel (with its two pipes) **for every offer**
with no ceiling. The only limit was in `Josour.Egress.StreamLimiter` on the host. The **guest**, however, had no limit
at all: a hostile (or compromised) host could offer channels endlessly and so decide by itself how many channels the
guest's process creates.

**The fix:** a ceiling on the acceptance path too, at `_window.MaxConcurrentStreams` (256/128/64 by band, section 5 of
`docs/protocol.md`), and the excess is answered immediately with `OPEN_FAIL(limit)` with no policy call and no socket
opened. The host's limit in `StreamLimiter` is finer (it includes 50 opens/second) and comes before it, so the host's
behaviour does not change.

The test: `OfferFloodBeyondTheStreamLimit_IsRejectedAsLimit` — with a concurrency limit of 8 and 64 offers, **56** were
answered with `limit`, memory stayed bounded, and the stream counter returned to zero.

### F-3 — a tunnel failure becomes an unobserved task exception (low, fixed)

Found by the unobserved-exception watcher in the fuzz harness. `Completion` is watched by
`TunnelSession.WatchAsync`, but there are paths where nobody watches it: the role-specific end failing to be built
after the mux is created (an immediate dispose), and every use without `TunnelSession` (the Spike tool, the tests). The
result is a `MuxClosedException` raised by the finaliser thread as an unobserved exception.

With .NET's default configuration this is noise rather than a crash, but it is **a process crash** on any host that
enables `ThrowUnobservedTaskExceptions`, and it muddies any investigation of a future fault.

**The fix:** a silent observer attached once in `Start()`. The real observers are unaffected: they all read the same
task.

### F-4 — a relay token that is not valid UTF-8 was silently rewritten (low to medium, fixed)

`RelayProtocol.TryParsePreamble` decoded the token with the default `Encoding.UTF8`, which **replaces** every invalid
byte with U+FFFD and never throws. So the `"token is not valid UTF-8"` branch was dead code, and the parser handed
verification a token that had been **rewritten** rather than the token that arrived: the wire does not refuse a corrupt
payload but silently changes it.

**The fix:** `UTF8Encoding(false, throwOnInvalidBytes: true)` when decoding (`DecoderFallbackException` inherits from
`ArgumentException`, so the existing handling catches it). The tests cover five shapes: a continuation byte with no
lead, a multi-byte lead followed by an invalid continuation, an encoded surrogate half, a truncated three-byte
sequence, and `FF FE` — against the parser and the reader alike.

### F-5 — the inbound-connection rows in the diagnostics had no ceiling (medium, fixed)

`SymmetricConnector._attempts` added a row for every inbound TCP connection during the connect window (30 seconds) with
no limit. The limit of four pending connections in `TunnelListener` bounds **concurrency**, not the total, so a peer
that opens and closes quickly generates thousands of rows. Its effect is twofold: memory growth driven by an outside
party, and `session.connect_failed` exceeding the 64 KB limit in `docs/api.md` **so the whole diagnostic is refused
with a 422** — that is, flooding the port erased the evidence of itself.

**The fix:** a ceiling of 16 rows for inbound connections (`SymmetricConnector.MaxRecordedInboundAttempts`), and an
`inbound_dropped` field with the number dropped. The outbound (dial) rows are not limited, because their number = the
number of the other side's candidates. And the excess is still **counted** in `inbound_attempts` and
`inbound_unauthenticated` and in the attempts log (section 4).

### F-6 — the liveness PING held an entry with no time limit (low, fixed)

`LivenessLoopAsync` fired `PingAsync(_cts.Token)` with no timeout of its own, and `PingAsync` waits for a PONG until
the token is cancelled — that is, **until the tunnel dies**. So every PING with no PONG left an entry in
`_pendingPings` **and a cancellation registration on `_cts`** that lived as long as the tunnel. That does not grow
without bound while the death timeout (60 seconds) works, but it is growth driven by the other side in a window that
serves no purpose.

**The fix:** `LivenessPingAsync` bounds the wait with `DeadAfter`, so the entry and the registration are released.
Judging death stays the loop's job through `_lastPongMs` as it was. And `NerdbankMux.PendingPings` was added
(diagnostics, not behaviour) so the soak test can watch it at every sample.

### Properties that were pinned and were not defects

- **Replaying AUTH1 is accepted from the same handshake.** There is no host nonce in section 4 of
  `docs/protocol.md`, so a valid AUTH1 message is accepted whenever it is replayed literally. The protection is two
  layers outside the handshake: the message is not seen at all because it is inside TLS pinned to the host's
  fingerprint, and the host sends AUTH2 only to the first connection that passes AUTH1 (`TryClaim`). The property was
  pinned by an explicit test so it does not change silently, and so that changing it is a decision rather than an
  accident.
- **The two failure paths in AUTH1 are not distinguishable by timing.** The measurement is end to end (including
  throwing the exception), because that is what an attacker actually sees. The medians: a wrong secret 34.60/34.40 µs,
  a wrong session id 34.50 µs — a difference of **0.00 µs** against a noise floor of **0.20 µs** (two halves of the
  same input). And the positive control: a wrong version (an early return declared in the contract) is measured at
  **0.50 µs**, which means the probe would see an early return if there were one.
- **Third-party noise.** `Nerdbank.Streams`'s internal tasks (`Channel.AutoCloseOnPipesClosureAsync`) fault with an
  unobserved `IOException` when the transport dies during an outbound write. We cannot fix it, so the fuzz harness
  classifies what it catches into "ours" (which must be zero) and "the library's" (reported as a count). In the fast
  suite: **0 of ours**, and 0–1 from the library depending on the run.

---

## 4. Reporting unauthenticated connections on the listener

The tunnel's listener (`TunnelListener`) is open only between `session.created` and `session.connected`, and it is
**the only place** where the system sees an unauthorised attempt to reach the machine's port. None of that passes
through the server, so the server cannot observe it: if the client does not report it, it is never observed.

**What counts as an attempt:** an inbound connection closed before passing `AUTH1` — whether the listener refused it
for exceeding the four pending (closed with not a byte read), or the TLS handshake failed, or the AUTH1 verification
failed, or it timed out.

**What does not count, and why** — the signal cannot bear one false positive, because one in every successful session
robs it of its meaning entirely:

1. **A connection that passed authentication**, even if it then lost the race (`superseded`).
2. **A connection we cancelled ourselves** because another connection won or the connect window closed (`cancelled`).
3. **A connection the other side closed with no reply before the authentication message completed.** This exception is
   the most important, and an intermittent test exposed it: the symmetric connection opens **two connections** between
   the two machines (each connects to the other and listens), and one of them loses **in every successful session**;
   and section 2 step 5 requires the host to "close any other connection with no reply". So the losing side sees a
   silent close after sending AUTH1 — which was being counted as an unauthorised attempt, giving
   `listener_unauthenticated: 1` in healthy sessions. `AuthFailedException.PeerClosed` now distinguishes "closed with
   no reply" from "verification failed", and the first is not counted.
4. **Any failure after the first successful authentication on this session**: from that moment every failure is race
   debris rather than evidence.

**The price, knowingly:** a port scanner that completes the TLS handshake and then closes without speaking is not
counted. And what is counted includes every scan that does not complete TLS (the overwhelming majority: a socket opened
with nothing written into it, or with bytes written that are not a ClientHello), and every timeout, and **every
verification failure** — which is the strongest evidence possible: a party that knows the message's shape and does not
know the secret.

**What is stored:** a counter and distinct source addresses capped at ten, and nothing else. No payloads, no domain
names, no source ports and no timestamps (`docs/api.md` requires that explicitly, as does section 15 of the product
document). Mapped `::ffff:a.b.c.d` addresses are normalised to IPv4 so the same address does not appear twice on a
DualMode listener.

### The keys track C reads

Straight from `ITunnelSession.Diagnostics`, and placed in `data` at `POST /api/v1/diagnostics` **as they are, with no
renaming and no arithmetic**:

| Key | Type | Notes |
|---|---|---|
| `listener_unauthenticated` | `int` | The total. Always present after `PrepareAsync` — and **zero** when there were no attempts, because the zero is the denominator of the rate at the server |
| `listener_port` | `int` | The port it was listening on (written in `PrepareAsync`) |
| `unauthenticated_peers` | `List<string>` | Distinct IP addresses in order of appearance, capped at 10 (`UnauthenticatedProbeLog.MaxPeers`) |
| `unauthenticated_peers_distinct` | `int` | **An optional addition outside the contract**: how many distinct addresses were actually seen before the cap of ten. Reported or dropped as track C prefers |

The first three are the reserved keys in `docs/api.md`; the server writes a `security_events` row only for a positive
value. The keys are written twice in the lifecycle: after `ConnectAsync` (when the listener has been closed so the
count is final even if the connection failed), and at step 4 of the cleanup order (to cover a session that ended before
`ConnectAsync` at all).

And in `SymmetricConnectOutcome.Diagnostics` (the connection's diagnostics, under the `connect` key) there are two
accompanying fields: `inbound_unauthenticated` with the same count, and `inbound_dropped` with the number of inbound
rows dropped because of the ceiling (F-5).

### The Spike tool

The `session` command reports it itself at the session's end, for both roles (the listener is open on both sides, not
on the host alone):

- The `listener.unauthenticated` event in the JSONL stream, with the count, the number of addresses and the port.
- Then `POST /api/v1/diagnostics` with `{session_id, role, data}` and the `diagnostics.posted` event with its result.
- Purely best effort: it does not throw, does not change the exit code, and its timeout (15 seconds) is **independent
  of the cancellation token**, because the usual reason for reaching this point is Ctrl+C — so the token is already
  cancelled and the report is still wanted.
- `--no-diagnostics` disables it.

**What track C must do:** read the three keys from `ITunnelSession.Diagnostics` at the session's end and put them in
`data` through its own path to `POST /api/v1/diagnostics`. No arithmetic, no aggregation and no filtering: the values
are ready and bounded by the limits the contract imposes.

**The state of the wiring as this document is written:** track C has already built its adapter in
`Josour.Infrastructure/Diagnostics/ListenerAuthDiagnostics.cs`, and it reads the same three names and accepts a
`List<string>` for the addresses and an `int` for the counter — so the two sides are compatible with no change. Two
deliberate differences deserve to be decisions rather than accidents:

1. **Track C sends only on a positive counter**, and the Spike tool always sends (including the zero). The difference
   is real: without the zero the server has no denominator, so "ten attempts" stays a number with no meaning. The
   decision belongs to tracks A and C together.
2. **Track C does not pass `unauthenticated_peers_distinct`** because it is outside the reserved keys. A sound choice;
   and the key stays available in `Diagnostics` for whoever wants it later.

---

## 5. The soak results

The machine: macOS on ARM, .NET 8.0.424, a Debug build. The two runs were in parallel in separate processes, each 32
minutes with a sample every 15 seconds (128 samples per run) on a simulated link with an RTT of 150 ms and a 2 MiB
window (the middle band).

### 5.1 A tunnel under load — `working-tunnel`

The load over 32 minutes: **2355 streams** opened and closed (of which **235 were terminated abruptly mid-transfer**),
one long-lived download, idle gaps of 20 seconds every two minutes, and **16.92 GB** transferred.

| Metric | First half | Second half | Difference | Minimum | Maximum | Slope/hour |
|---|---|---|---|---|---|---|
| Managed memory (after a full collection) | 7.72 MiB | 7.79 MiB | **+0.07 MiB** | 6.05 | 12.42 | +0.22 MiB |
| Working set | 102.4 MiB | 102.9 MiB | +0.5 MiB | 94.1 | 105.8 | +3.4 MiB |
| Threads | 32 | 31 | **−1** | 27 | 41 | −4.8 |
| Open file descriptors | 197 | 197 | **0** | 191 | 197 | +0.5 |
| Open streams (guest / host) | 2 / 1 | 2 / 1 | 0 / 0 | 1 / 1 | 2 / 2 | ≈0 |
| Pending PINGs (guest / host) | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | **1 / 1** | ≈0 |
| Latency p50 | 302.48 ms | 302.40 ms | **−0.09 ms** | — | 308.45 | +13.1 ms |
| Latency p95 | 304.67 ms | 304.35 ms | −0.32 ms | — | 309.76 | +12.7 ms |

**Nothing grows monotonically.** Memory oscillates between 6.05 and 12.42 MiB with no trend (the difference between the
halves is 0.07 MiB = 0.9%, two orders of magnitude smaller than the oscillation's own amplitude). The file descriptors
are nailed at 197 from the second minute to the thirty-second despite 2355 channels being opened and 235 of them
terminated abruptly — and that is the most important column here: channels terminated with `RST` leave behind neither a
socket nor a pipe. The threads move between 27 and 41 with the thread pool's own movement and then come back. The
pending PINGs never exceed **one** at any moment, which is exactly the right value: one request in flight on a 150 ms
link (after the F-6 fix).

**The latency is constant**: 302.4 ms as the median in the first minute and in the last, that is **2 × RTT** exactly —
a round trip to open the channel and a round trip for the request and response. No drift with time and none with the
number of streams opened earlier.

**And after all of it:** both stream counters returned to zero, and the tunnel opened a new stream, carried 64 KiB and
answered a PING.

### 5.2 An idle tunnel — `idle-tunnel`

No traffic at all for 32 minutes: PING/PONG every twenty seconds only (about 96 cycles in each direction).

| Metric | First half | Second half | Difference | Minimum | Maximum | Slope/hour |
|---|---|---|---|---|---|---|
| Managed memory (after a full collection) | 4.85 MiB | 4.86 MiB | **+0.02 MiB** | 4.61 | 4.88 | +0.08 MiB |
| Working set | 93.9 MiB | 94.3 MiB | +0.4 MiB | 79.4 | 94.4 | +8.5 MiB |
| Threads | 23 | 22 | **−1** | 21 | 25 | −2 |
| Open file descriptors | 197 | 197 | **0** | 189 | 197 | +0.5 |
| Open streams | 0 | 0 | 0 | 0 | 0 | 0 |
| Pending PINGs (guest / host) | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | **1 / 1** | ≈0 |

Managed memory moves within a range of **0.27 MiB** over half an hour (4.61 → 4.88). The liveness loop accumulates
nothing: not in the PING map (a peak of 1), not in the threads, and not in the file descriptors. And the tunnel stayed
alive (no `IsClosed` on either end) and **usable afterwards**: it opened a stream, carried 64 KiB and answered a PING.

### 5.3 The session's lifecycle — `session-lifecycle`

One tunnel, however long it lives, creates only one certificate and one listener, so the system resources the session
owns need a different kind of soak: repeating the cycle itself. **6435 complete `PrepareAsync` → `EndAsync` cycles** (a
certificate + a listener + candidates + the six-step cleanup order) in five minutes, 19 samples:

| Metric | Start | End | Minimum | Maximum |
|---|---|---|---|---|
| Managed memory | 5.58 MiB | 5.58 MiB | 5.58 | 9.17 |
| Open file descriptors | 197 | 197 | 193 | 197 |
| Threads | 22 | 22 | 20 | 35 |
| gen1 / gen2 collections (with the probe's own subtracted) | 134 / 34 | 134 / 34 | — | — |

And for the certificate alone at larger numbers: **2000 create-and-dispose cycles**, file descriptors **196 → 196**,
and settled memory 5.6 → 5.5 MiB, at **49 milliseconds per cycle** (generating the ECDSA P-256 key is the entire cost).
On macOS there are no key containers to check; this test is ready to run on Windows, where the question is real (see
section 6).

### 5.4 No performance regression

The `MuxBenchmarks` and `WanBenchmarks` from weeks 4 and 5 run as they are after this week's changes (21
`Category=Benchmark` tests, all passing). The governing numbers are unchanged to speak of:

| RTT | The derived window | Week 5 | Week 6 | Efficiency |
|---|---|---|---|---|
| 50 ms | 1 MiB × 256 | 162 Mbit/s | **161 Mbit/s** | 100% |
| 150 ms | 2 MiB × 128 | 113 Mbit/s | **112 Mbit/s** | 101% |
| 300 ms | 4 MiB × 64 | 113 Mbit/s | **112 Mbit/s** | 102% |

And it matters particularly that the **256 concurrent channels** benchmark still passes after the acceptance-path
ceiling was added (F-2): the ceiling is at exactly 256 in the lowest band, so the 256th channel is accepted and the
257th is the one refused.

### 5.5 A note on the collection columns

In these two runs the `Gen0/1/2` columns carried **the probe's own forced collections** alongside the application's:
measuring settled memory forces three full collections per sample, and on the idle tunnel they were all that appeared
(`Gen0 = Gen1 = Gen2` in every sample, which is the signature of a forced full collection, not of an application's
allocation). That was fixed after the two runs: `ProcessProbe` now counts what it performs itself and subtracts it from
the sample, so the column measures the application's allocation pressure alone. **All the other columns are
unaffected**, because they are not measured by collection.

---

## 6. What remains unproven

**1. A multi-hour run.** The longest actual run here is 32 minutes. The tool is designed for four hours and more (the
JSONL is written during the run so nothing is lost if it is cut off, and no state accumulates in the tool itself), but
it **has not been run** for that long. What might appear there and does not in half an hour: a leak at below a megabyte
an hour (beneath the noise floor here), and the session certificate ageing out (`notAfter` = `expires_at` + an hour),
which cuts off any session that exceeds its declared duration.

**2. Windows.** Every number in this document is from macOS on ARM. What is unproven there:

- **`SessionCertificate`'s key containers.** `ReimportForSchannel` imports the PFX with `UserKeySet` (without
  `PersistKeySet`), so a user key container is created that `Dispose` must delete. The container is a Windows-only
  concept, and the test here measures nothing but file descriptors and memory. The test
  (`RepeatedCertificateCreateAndDispose_DoesNotLeak`, a thousand cycles) **is ready to run there** and must be
  accompanied by a manual check of the `%APPDATA%\Microsoft\Crypto` directory before and after the run.
- **The resource counter.** On Windows `Process.HandleCount` is used instead of counting `/dev/fd`: a different source
  covering all the kernel's handles rather than sockets alone, so the absolute values are not comparable with these —
  only the trend is.
- **Schannel instead of OpenSSL.** The fuzz does not go through TLS (it feeds the mux directly), but the soak test
  does, and a half-hour handshake on Schannel has not been tried.
- **The thread pool.** The thread column is governed by .NET's pool heuristics, which are different on Windows.

**3. A real network.** The link simulator gives delay, a bandwidth ceiling, jitter and head-of-line blocking, and does
not give: real loss with actual TCP retransmission, a congestion window or slow start, mid-path interruptions (a NAT
mapping expiring, a network change, sleep and wake), or varying MTU. Only a soak run between two real machines proves
that what was observed is not an artefact of the simulator — and that hangs on the technical prototype on Windows in
`docs/status-week5.md`.

**4. What the fuzz deliberately does not cover.** The TLS layer itself (the platform's `SslStream`), Nerdbank's frame
format from the inside (ADR-0006 delegates it, and the fuzz falls on our own boundaries), and the relay service on the
server (it was not built; the ADR-0003 gate had not opened yet). What was exercised of the relay is **the client side
and the preamble parser**, nothing more.

**5. The timing test's limits.** It measures a difference at a noise floor of 0.2 µs on this machine alone, and the
result is "no difference measurable here", not "no difference at all". A timing attack over the network faces noise
orders of magnitude larger, so this is a lower bound on confidence rather than a proof.

**6. Blind with no resource source.** If there is no `/proc/self/fd`, no `/dev/fd` and no `Process.HandleCount`,
`ProcessProbe.OpenHandles()` returns zero and the soak test skips that column's verdict instead of claiming success.
The `ProcessProbe_ReadsSomethingOnThisPlatform` unit in the default suite exposes the situation before "no leak"
becomes a sentence with no source.

**7. The security signal has not been tested against a real server.** Reporting `listener_unauthenticated` is tested on
the client side up to the boundary of `POST /api/v1/diagnostics`; writing the `security_events` row and its appearing
in `GET /admin/security-events` is track A's work and needs a joint run. And the realistic noise figures are not yet
known: how many attempts does a machine on a corporate network see during a 30-second connect window? If the number is
large, the signal needs a threshold at the server rather than at the client.

---

## 7. The numbers and the tests

### The deep fuzz run (`Category=Benchmark`)

| Suite | Cases | Result |
|---|---|---|
| `Deep_MutatedWire` | 1500 | 1147 faulted with `MuxClosedException`, 353 closed cleanly (a mutation that left the GOAWAY intact); the heap 5220 → 5258 KiB |
| `Deep_MutatedWire_Fragmented` | 400 | 400 faulted with `MuxClosedException`; the heap 5294 → 5465 KiB |
| `Deep_RandomControlBytes` | 600 | 589 faulted, and in 11 the tunnel stayed alive (a payload under nine bytes, or frames valid by chance) |
| Total | **2500** | **Zero** hangs, **zero** exception types outside `MuxClosedException`, **zero** unobserved exceptions of our own making |

`ROUTEBRIDGE_FUZZ_CASES` raises the count for any longer run with no recompile.

### The test counts in track B's projects

| Project | Before | After | Increase |
|---|---|---|---|
| `Josour.Core.Tests` | 360 | 360 | — |
| `Josour.Tunnel.Tests` | 173 | **264** | +91 (72 fuzz, 12 for the attempts counter, 7 units for the soak tool) |
| `Josour.Egress.Tests` | 59 | 59 | — |
| `Josour.Proxy.Tests` | 183 | 183 | — |
| `Josour.Browser.Tests` | 38 | 38 | — |
| `Josour.E2E.Tests` | 20 | 20 | — |
| **Total** | **833** | **924** | **+91** |

And outside the default suite: **21 `Category=Benchmark` tests** (the mux and WAN benchmarks from weeks 4 and 5, the
three soaks, and the three deep fuzzes).

**Verification:** a build with no warnings on every owned project (Debug and Release), and three consecutive full runs
with no failures under the `Category!=Benchmark` filter. The `Josour.Tunnel.Tests` suite's time became **38–41 seconds**
(it was 31), and the increase is all from the fast fuzz and the attempts counter.

---

## 8. Proposals for the contract owners (not implemented from here)

These are notes on `docs/protocol.md` and `docs/api.md`, neither of which is owned by track B:

1. **Section 5 — what "a protocol error" means to the application.** The text today says `GOAWAY(protocol_error)` and
   closing the connection, and does not say that this is **an abnormal end** that the application must know about and
   report to the server. The code now does that (F-1), and the contract should state it in a sentence:
   "`GOAWAY(protocol_error)` — from either side — is an abnormal end: the death event is raised with the reason
   `protocol_error`, unlike `session_end` and `expired`."
2. **Section 5 — the stream limit on the acceptance path.** The text mentions "the host's limits" only, and the limit
   today is enforced on **both sides** in `NerdbankMux` (F-2), because the guest had no limit in front of a hostile
   host. Proposed: "the limit applies to whoever accepts, whatever their role".
3. **Section 2 — the listener's limit bounds concurrency, not the total.** "Four pending unauthenticated connections"
   does not stop thousands of successive connections within the thirty-second window. The practical result (F-5) is a
   ceiling on the diagnostic rows at the client; the contract should say that the diagnostics are a summary rather than
   a complete log.
4. **`api.md` — the reserved keys describe "the host's listener".** The listener runs on **both sides** during the
   connect window, and the Spike tool reports the signal from both roles (`role` in the envelope distinguishes them).
   Proposed: generalise the wording to "the side's listener", and add the optional key
   `unauthenticated_peers_distinct` if the server wants to distinguish "ten addresses" from "ten out of forty-seven".
