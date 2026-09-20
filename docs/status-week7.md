# Week 7 status (2026-09-09)

The week the product started working. **Milestone M2 reached**: a full session between two machines on two different
networks, with the sites seeing the host's address.

## What was completed and verified

| Output | Verification |
|---|---|
| **The relay service** complete: the wire contract, token verification, pairing, byte pumping, the limits, Docker and CI | **51 tests**, ruff clean, and a live run of the built service |
| **Wiring it end to end**: the server issues a token per side, `session.created.relay` carries it, and the client races it against the direct path | 5 tests that stand up a session over a real relay with no direct path at all |
| **Measuring the relay** by week five's method | [`performance-relay.md`](performance-relay.md) |
| **The rename** to Josour: 340 files, 15 projects, runtime identifiers inside the contracts | Builds with no warnings, and the tests are green |
| **All sites through the host** ([ADR-0010](decisions/0010-route-all-through-host.md)) | 1310 tests on the client, 345 on the server |
| **Five failures closed** — detailed below | A regression test for each |

**The total:** 345 on the server, **1320** on the client (under the CI filter `Category!=Benchmark`), 51 in the relay.

> The `Category=Benchmark` tests — the soak, the fuzz and the measurements — **are excluded from an ordinary run**:
> `WorkingTunnel_DoesNotGrowOverTime` alone runs for **30 minutes** by default. Running the solution without the
> filter asks for an hour of deliberate work, and looks like a hang, because a soak is quiet by nature.

## Two decisions closed with data rather than estimates

**[ADR-0009](decisions/0009-relay-default.md)** closed a gate that had stayed open since week one. The measurement on a
real pair (Egypt ↔ Saudi Arabia): one `public` candidate per side, no UPnP and no public IPv6, and failure twice at the
connect timeout. The old model **discovers** existing reachability and does not **create** it, so when both ends are
behind CGNAT there is nothing to discover.

And a conceptual correction drove the decision: **hole punching is a cost optimisation, not a reliability feature** —
it reduces what goes through the relay and does not replace it, because it fails deterministically with CGNAT↔CGNAT.

**[ADR-0010](decisions/0010-route-all-through-host.md)** superseded ADR-0004: a list an administrator manages
contradicts the product's purpose, because a single page pulls its resources from dozens of subdomains.

## Five failures, each hidden in a different way

| The failure | Why it was not caught |
|---|---|
| **The relay's address is a hostname** and was passed to a transport that accepts only address literals, so it died before opening a socket | The test passed `127.0.0.1` — a literal, which takes the one path that works |
| **The Arabic build crashes at startup** on the product's own name in the `User-Agent` header | The failure lives in the service wiring in `Josour.App`, **which has no test project**, and it depends on the language |
| **A fast clock disables the accept and reject buttons**: the request counts as expired before the window appears | The counter compared the server's time with the machine's clock, while the application **knows the drift** and applies it everywhere else |
| **The proxy refuses every connection from the work browser**: a configured browser + a checker that recognises nobody + refusing the unknown | A correct default for the tests that wrote it, wrong for the one caller that relied on it |
| **The request window shows a false sentence**: "only the company's listed sites are reachable" above "they will be able to browse any site" | Static text in XAML that did not follow ADR-0010 when everything else did; and the window is **the one place with no test project** |

**The common thread:** three were caught by making the software say what it already knows — the proxy's counters, the
browser diagnostic script, and the calibrated counter. The fourth by running the published file rather than by a test.
**And the fifth by reading the text before the manual round**: had the round started, item 4 would have fallen after a
full session on two machines, instead of two minutes of reading.

And three of the five — the Arabic build, the counter and the window — live in `Josour.App`. **The debt has one name,
and it has collected three times.**

## What was stopped from recurring

- **A proxy configuration that accepts nobody** refuses construction from the start, with a message saying what to
  change.
- **The client does not judge a request it never saw alive to be expired**: clock drift became a wrong number rather
  than a broken product.
- **The counter's arithmetic left the WPF window** for a tested layer, **and the disclosure text followed it** into
  `DisclosureText`.
- **Half a relay configuration stops the server** at startup instead of falling silently back to direct.

## The benchmark says what it cannot

The relay is not the bottleneck: **2 Gbit/s** for one session (nine times the fastest thing measured for the tunnel),
added latency **below a millisecond**, pairing at **0.6 ms**, and fairness between twenty sessions within **0.2%**.

But the measuring machine **reports neither CPU nor the process's memory** — a process that burned 4 seconds reports
zero, and another holding 300 megabytes reports 6.1. The columns are omitted rather than filled with zeros, and the
tool **checks that itself**, so they appear automatically on the deployment host. **ADR-0009's reservation about
asyncio's CPU cost is still open.**

## What needs human intervention

1. ~~The code-signing certificate~~ — **settled 2026-09-10: it is not bought**
   ([ADR-0011](decisions/0011-no-code-signing-certificate.md)). Three to five known users, and an OV certificate does
   not remove SmartScreen at that number. Distribution is one file whose hash is matched, and milestone M4 left the
   critical path.
2. **The 18-item acceptance list** — [filled in from the logs and then stopped by decision](acceptance-checklist.md) on
   2026-09-10 at: **5 met, 6 partial, 6 untested, and one that will not be implemented**. The rest closes with a single
   15-minute session whenever it resumes.
3. **Accepting [ADR-0001](decisions/0001-ui-framework-wpf.md)** — still "proposed" after the whole project was built on
   it.
4. **Running `python -m bench` on the deployment host** — one command that closes half of ADR-0009's reservation.

## Recorded debts

- **No test project for `Josour.App`**: three of the five lived there. What is moved out of it into a tested layer gets
  covered, and what stays in XAML nobody sees until a human reads it.
- **Two environmental tests** fail under load, hiding regressions in a full run. The third was not environmental but was
  testing the operating system: `Port_IsAssigned_AndStopClosesTheSocket` required a `SocketException` after closing the
  listener, and Windows 11 on ARM64 **drops the SYN** rather than refusing it, so **every** run on such a machine
  fails. It now requires that the connection **does not succeed** — which is the real claim — however the system
  announces the port is closed.
- ~~`cgnat_suspected` is a false negative~~ — **fixed 2026-09-10.** "Not checked" was separated from "not present" with
  `cgnat_checked`, and `nat_reachability` was added, which is answered even when CGNAT cannot be judged. 18 tests, and
  verification on the machine itself: the report became `checked=false, reachability=blocked` instead of a silent
  `false`.

## Week 8 (from the plan)
The joint security review, running the 18-item acceptance list, and E2E across the device matrix.
