# The tunnel's performance under realistic conditions (week 5)

Owner: track B. It extends the numbers in `docs/decisions/0006-multiplexing-library.md`, which were measured over
loopback at RTT ≈ 0, and carries out column B of week 7's row in the plan (12.3): "performance (heavy pages, video, 256
streams, an international RTT)", pulled forward.

**The conclusion in two lines:** a single stream's ceiling is exactly **the window ÷ the RTT**, and it was achieved at
99–102% efficiency in the measurements recorded below (±2 points between runs). With a 1 MiB window that means
163 Mbit/s at 50 ms, and **56 Mbit/s at 150 ms**, and **28 Mbit/s at 300 ms** per stream. That is enough for everything
a work browser does except one case: **one large download on a fast link across an intercontinental RTT**. The
recommendation is in section 7, and what was actually implemented from it, after the contract was amended, is in
section 8 (56 → 113 Mbit/s at 150 ms, and 28 → 113 at 300).

---

## 1. Usage

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"

# every benchmark (the ADR-0006 benchmarks over loopback + the week-5 benchmarks at a simulated RTT). About 4 minutes.
dotnet test client/tests/Josour.Tunnel.Tests -c Release \
  --filter 'Category=Benchmark' --logger "console;verbosity=detailed"

# one benchmark
dotnet test client/tests/Josour.Tunnel.Tests -c Release \
  --filter 'FullyQualifiedName~WanBenchmarks.PerStreamThroughput' --logger "console;verbosity=detailed"

# the default run (with no benchmarks) stays as fast as it was
dotnet test client/tests/Josour.Tunnel.Tests --filter 'Category!=Benchmark'
```

The printed lines start with `[throughput]`, `[derived]`, `[page]`, `[video]` and `[256]`, and they are the source of
the tables below, verbatim. The video benchmark runs 60 seconds by default; `RB_VIDEO_SECONDS=10` shortens it during
development (the numbers recorded here are from the full run).

**The files:**

| File | Role |
|---|---|
| `client/tests/Josour.Tunnel.Tests/Perf/LinkSimulator.cs` | The link simulator (delay, jitter, a bandwidth ceiling, loss) |
| `client/tests/Josour.Tunnel.Tests/Perf/LinkSimulatorTests.cs` | Tests of the simulator itself (fast, with no Benchmark tag) |
| `client/tests/Josour.Tunnel.Tests/Perf/PerfSupport.cs` | A mux pair over the link, the resource protocol, the page file, and a paced destination |
| `client/tests/Josour.Tunnel.Tests/Perf/WanBenchmarks.cs` | The five benchmarks (including the derived-window benchmark in section 8) |

---

## 2. The method

### 2.1 The link simulator

`LatencyStream` is a `Stream` wrapper placed **between the raw socket and TLS**, so the composition becomes:

```
a loopback socket ← LatencyStream ← SslStream ← NerdbankMux ← the application's streams
```

Every write is scheduled on a `LinkMedium` (a one-way wire) that computes its absolute arrival time, and it is then
delivered to the socket at that time. The link is full duplex: a medium per direction. The arrival times are
**absolute** rather than cumulative, so one timer error does not accumulate onto what follows.

What is modelled, and why to this extent:

- **Delay:** half the RTT in each direction. This is the governing variable for a windowed protocol.
- **The bandwidth ceiling:** a shared serialisation queue — the next write does not start before the previous one has
  left the wire. Several streams share one `LinkMedium` instance and so share one bottleneck, as on a real link. When
  the queue exceeds a second, the writer is slowed (a bounded buffer instead of unbounded growth).
- **Jitter:** variation in the arrival time **with no reordering**. The receiver behind TCP never sees reordering —
  TCP sees it and hides it — but sees variation in when the bytes are delivered. Arrival is constrained to be
  monotonic for that reason.
- **Loss:** bytes cannot be "dropped" from a reliable stream without corrupting it, and dropping them measures
  something that does not happen. Loss appears to the layer above as **head-of-line blocking**: the lost packet delays
  everything after it until it is retransmitted. So it is modelled as a cumulative delay (an RTO per lost packet)
  affecting all the following bytes. `LinkSimulatorTests.Loss_AddsDelay_ButNeverCorruptsOrDrops` proves the stream
  arrives complete and intact and late.

**What is not modelled:** TCP's congestion window and its slow start. That is, the numbers are optimistic by the first
few RTTs of every TCP connection, and that **favours the direct path rather than the tunnel**: the tunnel is one
long-lived connection paying one slow start in its whole lifetime, while the direct path pays it for every new
connection. So the verdict in section 4 that "the tunnel adds nothing" is conservative.

**The simulator itself is measured:** six fast tests prove it adds the delay it claims, respects the ceiling, shares
the wire between connections, and does not reorder under violent jitter. And every benchmark prints **the RTT measured
by PING through the whole stack** alongside the nominal one; the difference between them stayed ≤ 4 ms in every run,
and that is the measurement error in the numbers below.

### 2.2 The environment

Apple M5 (10 cores), macOS 26.6.2, .NET 8.0.424, a **Release** build, TLS **1.2** (what the stack negotiated on this
platform; printed in every measurement's output), a 1 MiB receiving window per stream except where stated otherwise.
The numbers come from one complete run on 2026-09-05.

### 2.3 The numbers' limits

- **One machine, two logical processes inside one process.** No Schannel, no real TCP stack between two cities, and no
  real congestion on the way. The simulator gives an honest RTT and an honest bandwidth ceiling, not an honest network.
- **No TLS to the origin and no DNS** in the page benchmark, on both paths. The aim is to isolate the tunnel's
  contribution, not to simulate a whole browser.
- **Windows has not been measured yet** (Schannel, TLS 1.2 on Win10). See section 9.

---

## 3. The per-stream throughput ceiling

`PerStreamThroughput_IsBoundedByWindowOverRtt`. One download on one stream with no bandwidth ceiling: the only
constraint is the window and the RTT.

| Nominal RTT | Measured RTT | Window | Size | Time | Measured | The ceiling = window ÷ RTT | Efficiency | OPEN time |
|---|---|---|---|---|---|---|---|---|
| 50 ms | 51.6 ms | 1 MiB | 64 MB | 3174 ms | **20.16 MB/s (161 Mbit/s)** | 20.32 MB/s (163 Mbit/s) | 99% | 53 ms = 1.0 RTT |
| 150 ms | 152.2 ms | 1 MiB | 28 MB | 3950 ms | **6.98 MB/s (55.8 Mbit/s)** | 6.89 MB/s (55.1 Mbit/s) | 101% | 151 ms = 1.0 RTT |
| 300 ms | 300.4 ms | 1 MiB | 14 MB | 3940 ms | **3.54 MB/s (28.3 Mbit/s)** | 3.49 MB/s (27.9 Mbit/s) | 102% | 301 ms = 1.0 RTT |
| 150 ms | 153.8 ms | **4 MiB** | 64 MB | 2290 ms | **27.95 MB/s (224 Mbit/s)** | 27.27 MB/s (218 Mbit/s) | 102% | 152 ms = 1.0 RTT |
| 300 ms | 303.4 ms | **4 MiB** | 55 MB | 3946 ms | **14.01 MB/s (112 Mbit/s)** | 13.82 MB/s (111 Mbit/s) | 101% | 302 ms = 1.0 RTT |

**The reading:** the formula applies without qualification. There is no other bottleneck — not in Nerdbank, not in
TLS, not in `StreamPump` — up to 224 Mbit/s on a single stream. The efficiency above 100% in some rows is the link's
buffer (bytes on the wire when the clock stops) and the 1–4 ms difference between the nominal and the measured RTT;
it is not a miracle.

**The cost of opening a stream = exactly one RTT**, precisely what a direct TCP connection pays. That is an important
number because it means the mux adds no extra round trip before the first byte, which is what explains section 4's
result.

---

## 4. A heavy page

`HeavyPageLoad_TunnelVersusDirect`. A 60 KiB document, then **80 subresources** of mixed sizes (8 scripts × 120 KiB,
6 stylesheets × 40 KiB, 40 images × 25 KiB, 20 fonts × 12 KiB, 6 XHR × 8 KiB) = **2548 KiB** spread over **30
concurrent paths**, each reused for its resources in sequence (the equivalent of keep-alive, which is the browser's
behaviour with 6 connections × ~5 assets).

The "direct" baseline: the same resources on the same link with the same shared bottleneck, with a TCP connection per
path paying **a handshake RTT** before the first request, and with no mux. The clock stops at the last resource byte
rather than after the connections are torn down — that is, the page load time as the user sees it.

| The link | First OPEN | The document (tunnel) | The document (direct) | The whole page (tunnel) | The whole page (direct) | **The tunnel's overhead** |
|---|---|---|---|---|---|---|
| RTT 50 ms, no ceiling | 52 ms = 1.04 RTT | 105 ms | 103 ms | 319 ms | 311 ms | **+2% (7 ms)** |
| RTT 150 ms, no ceiling | 152 ms = 1.01 RTT | 302 ms | 304 ms | 925 ms | 912 ms | **+1% (13 ms)** |
| RTT 300 ms, no ceiling | 302 ms = 1.01 RTT | 613 ms | 602 ms | 1829 ms | 1812 ms | **+1% (17 ms)** |
| RTT 150 ms, 50 Mbit/s | 155 ms = 1.03 RTT | 316 ms | 314 ms | 1158 ms | 1156 ms | **+0% (2 ms)** |
| RTT 150 ms, 10 Mbit/s | 162 ms = 1.08 RTT | 363 ms | 356 ms | 2718 ms | 2699 ms | **+1% (20 ms)** |

**The reading:** the overhead the tunnel adds to a heavy page is **within the measurement noise** (0% to 2%, that is,
2 to 20 ms on a page that takes a third of a second to three seconds). The reason is direct: the cost of opening a
stream = one RTT = exactly the cost of a TCP handshake, and a request/response round trip on a warm stream =
**1.00–1.04 RTT** (measured independently in the same benchmark), and the 1 MiB window is far larger than any resource
in the file. And the page at 300 ms costs 1.8 seconds on both paths; the RTT is the enemy, not the tunnel.

And the verdict does not change with a bandwidth ceiling: at 50 Mbit/s and 10 Mbit/s the wire becomes the bottleneck on
both paths, and the framing and encryption overhead (0.09% of the bytes per ADR-0006) stays under the measurement
noise.

---

## 5. A sustained constant-rate stream

`SustainedVideoStream_NoStall_NoMemoryGrowth`. One download at **5 Mbit/s for 60 seconds** (37.5 MB) at RTT 150 ms,
while **20 other streams** are active fetching 32 KiB every 200 ms throughout.

| Measurement | Result |
|---|---|
| Received | 37.5 MB in 60.0 s = **5.0 Mbit/s** (the requested rate with no shortfall) |
| Background traffic during it | 110.8 MB over 20 streams |
| Inter-arrival time: p50 | 0.0 ms (one chunk arrives across several successive reads) |
| p95 / p99 | 102.9 ms / 104.0 ms |
| **The largest gap** | **109 ms** — that is, the producer's own cadence (a chunk every 100 ms), not a stall |
| Managed memory | 15.2 MiB before → 30.3 MiB in the middle → **8.9 MiB after settling** |

**The reading:** no stall and no accumulating delay: the stream finished in its time rather than after it. The jitter
the consumer sees is the producer's own jitter; the tunnel added nothing measurable. And the memory came back **below**
the baseline, so there is no leak: the windows are allocated on demand (`System.IO.Pipelines`) and 21 × 1 MiB is not
reserved in advance.

---

## 6. 256 concurrent streams with stalled consumers

`Concurrent256Streams_SlowConsumersDoNotStarveTheRest`. The maximum in section 5 of `docs/protocol.md` (256 streams) at
RTT 150 ms, of which **32 have a completely stalled destination** (the consumer does not read), while the other 224
fetch 256 KiB each. ADR-0006 proved this isolation at RTT = 0 only, where the window is not exercised at all.

| Measurement | Result |
|---|---|
| Opening 256 streams | **177 ms = 1.1 RTT** in total (0.69 ms each, in parallel) |
| What each stalled stream accepted | **Exactly 1.00 MiB** of 8 MiB offered — that is, one window, with the rest held at the sender |
| What their destinations received | **0 bytes** (the consumer really is stalled) |
| The 224 healthy ones | 59 MB in **258 ms = 1.7 RTT** = 228 MB/s in total (1824 Mbit/s) |
| Draining the 32 after the stall is lifted | 8 MiB each, completed at 1403 ms |

**The reading:** the isolation works at a real RTT as it did at zero: the sender stopped at one window (1.00 MiB, not
8 MiB), and the other 224 finished their work in 1.7 RTT — that is, the stalled streams cost them nothing measurable.
And opening 256 channels costs one RTT in total because the opens are parallel.

**The theoretical memory ceiling at the maximum:** 256 streams × 1 MiB = 256 MiB if every window filled at once. It
happened in no measurement (section 5 measures 21 streams with a 30 MiB managed peak), but it is a real ceiling that
must be mentioned at any raising of the window — see section 7.

---

## 7. The verdict on the window, and the recommendation

### 7.1 Is 1 MiB enough?

For three cases out of four: clearly yes.

| Use | What it needs | 1 MiB at 150 ms (56 Mbit/s) | 1 MiB at 300 ms (28 Mbit/s) |
|---|---|---|---|
| Heavy pages | Parallel, every resource < 1 MiB | Enough (≤ 2% overhead) | Enough (≤ 2% overhead) |
| 1080p video | 5–8 Mbit/s | Enough by 7× | Enough by 3.5× |
| 4K video | 20–25 Mbit/s | Enough by 2× | **On the edge** |
| Downloading one large file | Everything the link gives | **Limited to 56 Mbit/s** | **Limited to 28 Mbit/s** |

The case that fails is explicit: **one large download over HTTP/2** (which is one TCP connection = one stream in our
tunnel) on a link of 100 Mbit/s or more with an intercontinental RTT. A user on a 100 Mbit/s link sees 56 Mbit/s at
150 ms and 28 Mbit/s at 300 ms, and will say "the tunnel is slower", and will be right. And that is not a defect in the
library nor in the code: it is the window multiplied by the reciprocal of the RTT. Hiding it inside a pleasant average
is deceptive.

**Nothing compensates for it automatically:** there is no connection-level window in Nerdbank protocol 3 (so the
aggregate ceiling is not constrained, which the total of 228 MB/s in section 6 confirms), but the **per-stream** ceiling
is constrained, and a single download is a single stream.

### 7.2 The recommendation

**A window derived from the RTT measured at connect time, with a session-level memory budget.** The three parts:

1. **Measure the RTT once when the tunnel starts.** The number is free: `connect_ms` in `session.connected`, or
   `IMuxEndpoint.PingAsync` right after the handshake.
2. **Choose the window from it** so that a single stream's ceiling stays **above 100 Mbit/s** (beyond which the user's
   own link becomes the constraint rather than us). Applying window ÷ RTT — a formula that held at 99–102% efficiency
   in section 3, so the arithmetic is not guesswork:

   | Measured RTT | Window | The resulting stream ceiling |
   |---|---|---|
   | ≤ 60 ms | 1 MiB (as today) | ≥ 140 Mbit/s |
   | 60–150 ms | 2 MiB | 112 Mbit/s at 150 ms |
   | > 150 ms | 4 MiB | **112 Mbit/s at 300 ms** (measured: 112.2) |

   `MuxOptions.ReceiveWindow` already accepts the value; the change is **who fills it**: `TunnelSession` after the
   handshake.
3. **Tie it to the stream limit.** 256 × 4 MiB = **1 GiB** as a theoretical ceiling, which is unacceptable on a desktop
   machine. Memory is allocated on demand rather than in advance (section 5 measures 30 MiB for 21 active streams), so
   the theoretical ceiling is rare — but a 1 GiB ceiling is not left unguarded. The cleanest is a session-level budget
   of buffered bytes shared among the streams; the simplest is lowering the concurrency limit as the window grows, so
   that the product stays fixed at 256 MiB.

**The cheaper alternative if adaptation is rejected:** pin the window at 4 MiB and lower the concurrency limit from 256
to 64. It covers every case with a 256 MiB memory ceiling and no adaptive logic at all, and 64 concurrent connections
is more than a browser opens for one page (30 in section 4's file). Its cost: changing two limits in section 5 of
`docs/protocol.md` instead of one.

**What we do not recommend:** leaving it at 1 MiB and relying on users not noticing. A difference of 3.5× on an
intercontinental link is visible to anyone downloading a single file.

**The contractual consequences:** the number 1 MiB is written in section 5 of `docs/protocol.md` and in ADR-0006, and
both need a deliberate amendment rather than a silent change in the code. So the default was not changed this week: the
measurement is here, and the decision is the contract owners'.

---

## 8. After the amendment: the window is derived from the RTT (measured)

The contract was amended (`docs/protocol.md` section 5) and the code followed: `MuxWindow.ForRoundTrip` in
`Josour.Tunnel.Mux` chooses the window from `connect_ms`, and `TunnelSession` calls it after `SymmetricConnector` and
passes the accompanying concurrency limit to `StreamLimiter` through `TunnelEgressContext.MaxConcurrentStreams`. The
50 opens/second limit is unchanged.

The new benchmark `WanBenchmarks.PerStreamThroughput_WithRttDerivedWindow_ClearsTheSingleDownloadCeiling` runs the same
download as section 3 but with the window the function itself chooses (not a number written into the test):

```bash
dotnet test client/tests/Josour.Tunnel.Tests -c Release \
  --filter 'FullyQualifiedName~PerStreamThroughput_WithRttDerivedWindow' --logger "console;verbosity=detailed"
```

| Nominal RTT | Measured RTT | The derived window | Concurrency limit | Size | Time | **Measured** | Efficiency | Before the amendment (1 MiB) | The gain |
|---|---|---|---|---|---|---|---|---|---|
| 50 ms | 52.0 ms | 1 MiB | 256 | 81 MB | 3975 ms | **20.29 MB/s (162.3 Mbit/s)** | 101% | 161.3 Mbit/s | ×1.0 |
| 150 ms | 150.6 ms | 2 MiB | 128 | 56 MB | 3947 ms | **14.11 MB/s (112.9 Mbit/s)** | 101% | 55.7 Mbit/s | **×2.0** |
| 300 ms | 301.0 ms | 4 MiB | 64 | 56 MB | 3929 ms | **14.19 MB/s (113.5 Mbit/s)** | 102% | 27.9 Mbit/s | **×4.1** |

The numbers are from a complete run on 2026-09-05 in the same environment as section 2.2 (M5, .NET 8.0.424, Release,
TLS 1.2). A second run the same day gave 162.9, **110.9** and 112.9 Mbit/s: that is, the middle row sits at **111 ± 1
Mbit/s**, because the measured RTT exceeds the nominal one by 1–3 ms and the ceiling drops by that much. So the
benchmark confirms "above 100 Mbit/s" and the ratio of the gain (×2 and ×4), not a literal number.

**The reading:** the one case that used to fail (downloading a single work file across an intercontinental RTT) no
longer fails: 56 → 113 Mbit/s at 150 ms, and 28 → 113 at 300 ms. The efficiency stayed at 101–102% as in section 3,
which means the larger window really does turn into throughput and no other bottleneck appears in its place. The lowest
band (≤ 60 ms) did not change from what it was: the same 1 MiB and the same 256 streams.

**What we bought that with — the memory ceiling:** the window × the concurrency limit = **256 MiB in all three bands**
(256×1, 128×2, 64×4), so the theoretical worst case has not moved from section 6. The real price is elsewhere:

- **More memory in the common case rather than at the ceiling:** a session at a high RTT carries windows of 2 or 4 MiB
  per active stream instead of 1 MiB. The sustained measurement (section 5, 21 active streams) reached 17.0 MiB managed
  with a 1 MiB window; the same load with a 4 MiB window reaches several times that if the windows actually fill.
- **Less concurrency where the RTT is higher:** 64 streams instead of 256 at 4 MiB. Section 4's page uses 30 paths, so
  there is margin, but a browser opening more than 64 concurrent connections will hit `OPEN_FAIL(limit)` on an
  intercontinental link where it would not have before the amendment.

**A warning for the contract owners — `connect_ms` is not the RTT:** the contract calls it "the round-trip time
measured when the connection is established" and the code implements that literally, but
`TunnelConnectResult.ConnectMs` measures **the whole connection**: the TCP handshake (an RTT) + the TLS handshake (one
RTT or two) + `AUTH1`/`AUTH2` (an RTT), on top of the candidate race and the time difference between when each side
started. That is, it approximates **three to four times** the real RTT. The practical result: many sessions will rise a
band or two above what the RTT alone justifies — at a cost in memory and concurrency rather than throughput. If the
exact number is wanted, the correct source is `IMuxEndpoint.PingAsync` (section 3 measures it to within ≤ 4 ms), but it
is only available **after** the mux is created and the window is fixed at creation; so using it requires an amendment
to the contract rather than to the code.

**A corrupt measurement:** a `connect_ms` that is zero, negative or greater than 5 seconds (a timeout, a clock that
jumped, a missing field) does not choose a window from rubbish: it takes the middle band (2 MiB / 128) with a reason
written into the session's diagnostics (`mux_window_reason`).

**Each side derives its own window:** both sides measure `connect_ms` each by its own clock, so they may differ
slightly and may — near a boundary — land in different bands. That is legal and needs no negotiation: the receiving
window in Nerdbank protocol 3 is a property of the **receiver** and is announced per channel in the offer/accept frame,
so each direction is governed by its receiver's window.
`NerdbankMuxTests.Windows_AreAdvertisedPerReceiver_SoTheTwoSidesMayDiffer` proves it by measurement: with a guest at
1 MiB and a host at 4 MiB, the Guest→Host direction stopped at the host's window and the Host→Guest direction at the
guest's, and the seeded channel (PING/PONG) worked in both directions.

But the two bands differing opens a hole in **the memory limit**: the host enforces its limit on the wire
(`StreamLimiter`), so if it were in the lowest band (256 streams) and the guest in the highest (4 MiB), the guest's
theoretical ceiling would reach 1 GiB — which is exactly what the amendment meant to prevent. So the guest reserves
each stream's slot in `NerdbankMux.OpenStreamAsync` before offering, and answers `OPEN_FAIL(limit)` locally when its
own band's limit is exceeded, so each machine's ceiling stays 256 MiB however the two bands differ. This local limit is
no tighter than what the contract allows the host in the same band, and the browser sees it as any other
`OPEN_FAIL(limit)`.

## 9. What still needs a real network or Windows

| Item | Why the measurement here is not enough |
|---|---|
| Schannel on Windows 10 (TLS 1.2) and 11 (TLS 1.3) | All the numbers are on .NET's own TLS stack on macOS. The encryption overhead and the record size differ |
| Real congestion and TCP slow start | The simulator does not model the congestion window; the numbers are optimistic by the first few RTTs of every connection |
| Real loss on an international path | Only head-of-line blocking was modelled; the real RTO behaviour of the system's stack may differ |
| The system's own TCP receive window | It may become the bottleneck before ours at a high RTT if window scaling is not enabled |
| Wi-Fi and mobile networks | Jitter and loss in patterns a fixed file does not represent |

And all of them need the same item pending since week one: **two real Windows machines** per `docs/spike-runbook.md`.
