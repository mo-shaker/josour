# The relay service's performance

Owner: track A. Carries out the last item in [ADR-0009](decisions/0009-relay-default.md): "**a measurement is required
before adoption**: the relay's throughput and latency on the chosen machine, by the same method used in
[docs/performance-week5.md](performance-week5.md). No number is adopted without measurement."

**The conclusion in three lines:** the relay is **not the bottleneck**. Its measured ceiling is around **2 Gbit/s** for
one session, that is ten times the fastest thing measured for the tunnel itself (224 Mbit/s in week five). And its cost
in time is **below one millisecond**, smaller than the measurement noise on a simulated link — meaning what the relay
adds in practice is **geography alone**, not software. And pairing costs **0.6 milliseconds**.

**But this measurement does not close ADR-0009's reservation in full.** The measuring machine reports neither CPU nor
memory per process (section 2.3), and those are exactly what the decision reserved judgement on in asyncio's pumping.
It stays open until it is run on the deployment host.

---

## 1. Usage

```bash
cd relay
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]" psutil

python -m bench                 # everything, about two minutes
python -m bench latency         # a single suite
python -m bench throughput concurrency
```

The printed lines start with `[env]`, `[latency]`, `[throughput]`, `[concurrent]` and `[pairing]`, and they are the
source of the tables below, verbatim.

| File | Role |
|---|---|
| `relay/bench/link.py` | The delay line (a bidirectional link with a set round-trip time) and RTT measurement |
| `relay/bench/__main__.py` | The four suites and the environment line |

The relay runs in **its own process** as it does in production, not inside the measuring process.

---

## 2. The method

### 2.1 The fair comparison: the same wire, with and without the hop

The relay adds time **by definition**: it stores and re-sends. But most of that is geography rather than software, and
a laptop cannot measure it. So what is measured here is what the software adds **on top of the wire**, and that alone
is what a change in the code can spoil.

And the comparison is only valid if both paths carry **the same total wire time**. So for a nominal round-trip time of
R:

```
Direct:  end ──(the whole R)── end
Relay:   end ──(R/2)── relay ──(R/2)── end
```

And that is exactly the case of a well-placed relay: it sits **on** the path, so its two legs are roughly equal to the
direct route (Egypt → Jeddah → Saudi Arabia). The difference that remains after subtracting the wire is the software's
cost.

### 2.2 Calibrating the tool itself

The first run gave 50 ms for a nominal time of 40, and 133 ms for 120 — **on both paths**. The cause is that Windows
splits timer waits into **15.6-millisecond** slices, which is larger than everything this file is trying to measure:
the relay's number was four sliced sleeps rather than four forwarding operations. After raising the resolution to 1 ms
with `timeBeginPeriod(1)`:

| Nominal time | Measured on the direct path | Error |
|---|---|---|
| 0 ms | 0.40 ms | 0.40 ms |
| 40 ms | 43.24 ms | +3.2 ms |
| 120 ms | 124.38 ms | +4.4 ms |

**That error (±4 ms) is the noise floor in section 3**, and it is larger than the measured effect itself. The numbers
in section 3 are read on that basis: the relay is cheaper than this tool can measure under simulated delay.

### 2.3 What this run cannot measure — and saying so is deliberate

The measuring machine **reports neither CPU nor memory per process**. That is not an inference but a tested fact:

- A process that burned **4 seconds of CPU** reported `0.0` from `psutil` **and from `GetProcessTimes` directly**.
- A process holding **300 megabytes** reported a constant `rss = 6.1 MB` — which is below the floor for a CPython
  process to begin with.

So the columns are omitted rather than filled with zeros. And the tool **checks this itself** at startup and prints the
result:

```
[env] per_process_cpu=UNAVAILABLE per_process_rss=UNAVAILABLE
```

So when this is run on the deployment host the columns appear automatically with no change. **And that means ADR-0009's
reservation — the efficiency of asyncio's pumping in Python — is not yet closed.** What is closed is the question of
throughput and latency, which is what threatened the transport decision.

### 2.4 The environment

Windows 11 (build 26200), ARM64, 12 logical cores, Python 3.12.10, the default asyncio loop, loopback. The numbers come
from one complete run on 2026-09-08.

**The numbers' limits:** one machine, three processes over loopback. No TCP stack between two cities, no real
congestion, and no packet loss. The delay line gives an honest latency, not an honest network. And the throughput
ceiling below is **the software's** ceiling, not the VPS's link, which is what will actually constrain production.

---

## 3. The latency the relay adds

`bench latency`. The same total wire time in both cases, with and without the hop.

| Nominal round-trip time | Direct p50 | Via relay p50 | Via relay p99 | The difference |
|---|---|---|---|---|
| 0 ms | 0.40 ms | 1.15 ms | 2.25 ms | **+0.75 ms** |
| 40 ms | 43.24 ms | 46.79 ms | 50.10 ms | +3.55 ms |
| 120 ms | 124.38 ms | 124.54 ms | 127.13 ms | **+0.16 ms** |

**The reading:** the first row is the only clean measurement — no simulated delay, so no tool noise. The hop's cost
there is **0.75 milliseconds** for a full round trip, that is about **0.19 ms per forwarding operation** (the round trip
crosses the relay four times).

The other two rows say one thing: the difference (0.16 and 3.55) **falls inside the tool's noise floor (±4 ms)** and
does not grow steadily with the nominal time. Had the relay had an appreciable cost it would have appeared consistently
and increasingly. That is, **what the user will feel from the relay is geography alone.**

And that makes [the location decision in ADR-0009](decisions/0009-relay-default.md) the governing variable in practice:
a relay in Germany makes the Egypt↔Saudi path about 150 ms, and one in Jeddah about 40 — a difference **two hundred
times larger than the software's cost**.

---

## 4. Throughput

`bench throughput`. One session, a bulk transfer, with no artificial delay: this is the pumping's own ceiling.

| Size | Time | Measured |
|---|---|---|
| 64 MB | 301 ms | **212.8 MB/s (1703 Mbit/s)** |
| 256 MB | 1021 ms | **250.7 MB/s (2005 Mbit/s)** |

**The reading, which is the answer to the most important reservation:** the fastest thing measured for the tunnel
itself in week five is **224 Mbit/s** on a single stream with a 4 MiB window. The relay's ceiling is **about nine times
that**. So pumping in Python asyncio is not the bottleneck by any reasonable margin — the constraint will be the VPS's
link, then the window and the RTT, not this service.

---

## 5. Concurrent sessions

`bench concurrency`. Every session transfers 32 megabytes at the same time.

| Sessions | Total throughput | Per session | The slowest | The fastest | The spread |
|---|---|---|---|---|---|
| 1 | 174.7 MB/s (1397 Mbit/s) | 174.7 MB/s | 183 ms | 183 ms | — |
| 5 | 284.0 MB/s (2272 Mbit/s) | 56.8 MB/s | 563 ms | 563 ms | **0.0%** |
| 10 | 299.4 MB/s (2395 Mbit/s) | 29.9 MB/s | 1069 ms | 1067 ms | **0.2%** |
| 20 | 287.1 MB/s (2297 Mbit/s) | 14.4 MB/s | 2229 ms | 2224 ms | **0.2%** |

**Two readings:**

1. **Total throughput settles at around 285 MB/s** from five sessions upwards. That is the single loop's ceiling
   (single-threaded `asyncio`), and it was reached at five sessions and did not degrade through twenty — that is, **no
   collapse under load**, but the sharing out of a fixed ceiling.
2. **Fairness is practically perfect.** The difference between the slowest session and the fastest is **0.2%**. No
   session starves. And that is an important property for a product where different users' sessions share one service.

**At this release's scale (5 users):** if all five browsed at once and each saturated their link, one session's share of
the software's ceiling is **56.8 MB/s = 454 Mbit/s** — several times what the VPS can push out to begin with. The
service is not the constraint.

---

## 6. The cost of pairing

`bench pairing`. From opening the two sockets to `paired` arriving at both ends, 50 samples.

| p50 | p99 | Max |
|---|---|---|
| **0.58 ms** | 1.62 ms | 1.62 ms |

**The reading:** pairing, verifying the token (HS256) and matching the two ends cost less than a millisecond. The
connect window is 30 seconds, so this is **fifty-thousandths of one percent of it**. It does not deserve optimisation,
and it deserves to be measured once to close the question.

---

## 7. What stays open

| Item | Why it is not closed | What closes it |
|---|---|---|
| **CPU per gigabyte** | The environment does not report the process's CPU (section 2.3) — which is the substance of ADR-0009's reservation | Running `python -m bench` on the deployment host; the columns appear automatically |
| **Memory per session** | The environment does not report the process's memory | The same |
| **Throughput on the real VPS link** | Loopback measures the software, not the network | A measurement between Egypt and Saudi Arabia over a Jeddah relay |
| **Behaviour under packet loss and real congestion** | Not modelled, and an incomplete model is worse than none | A real run |

**What is closed:** the relay is not a throughput bottleneck, adds no perceptible latency, neither collapses nor
treats a session unfairly under load, and pairing is cheap. And those were the risks that threatened ADR-0009's
decision to make it the default transport.
