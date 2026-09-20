# ADR-0009: the relay is the default transport, and direct is an opportunistic upgrade

**Status:** **accepted by the product owner on 2026-09-07.** Closes the decision gate in
[ADR-0003](0003-nat-traversal-and-relay-gate.md) item 4, and amends the product document (sections 6, 10 and **16**)
on "browsing data does not pass through the central server".

## Context

The ADR-0003 gate required building a relay if the direct-connection rate fell below 85%. The gate stayed open from
week one for want of a measurement on real networks. That measurement was taken on 2026-09-07 on a real pair
(Egypt ↔ Saudi Arabia):

| Side | Candidates | UPnP | Public IPv6 |
|---|---|---|---|
| Guest (Egypt, phone hotspot) | 1 — `public` only | `upnp_found: false` | `false` |
| Host (Saudi Arabia) | 1 | — | — |

The result: `connect_failed` after 25 seconds at the connection timeout, twice in a row. And on a single network the
connection succeeded on a `lan` candidate in **85 milliseconds** with TLS 1.2 — meaning the stack is sound and the
obstacle is reachability alone.

**The structural reason:** the ADR-0003 model **discovers** existing reachability, it does not **create** it. All four
of its candidate types are conditional: `lan` defeats the purpose, `v6` needs public IPv6 at both ends, `upnp` needs a
router with UPnP enabled (nonexistent on phone hotspots, off by default on many routers, forbidden in companies), and
`public` needs a public address or a manual port forward. So when both ends are behind CGNAT, **there is no direct
path at all**, and no configuration creates one.

Another governing fact also changed: the product owner defined the audience as **people with no technical
background**, and the requirement is that the program works **the moment it is installed**, with not a single step on
the router or the firewall. That requirement alone rules out the ADR-0003 model as the sole transport, regardless of
the measured rate.

## Decision

1. **The relay is the guaranteed default transport.** Every session runs over it unless a direct path wins.
2. **Direct stays and is attempted in parallel**, and wins when it succeeds (a single network, homes with UPnP, public
   IPv6). It is not removed: it is the fastest and cheapest when it is available, and the `ITunnelTransport` interface
   was built for this.
3. **No hole punching in this release.** The conceptual correction that drove the decision: hole punching is **a cost
   optimisation, not a reliability feature** — it reduces what goes through the relay, and never removes the need for
   it, because it fails deterministically with CGNAT↔CGNAT. Building the relay first keeps the promise; adding hole
   punching later lowers its bill.
4. **The relay lives in the Gulf.** The governing variable for performance became its location, not its capacity: a
   relay in Germany makes the Egypt↔Saudi path about 150 milliseconds, and one in Jeddah or Dubai about 40. It is
   deployed in a Gulf region.
5. **The firewall rule becomes optional.** With the relay, neither side needs to accept an inbound connection — both
   connect **outbound** on port 443. The rule stays in the installer to serve the direct path alone, and its absence no
   longer prevents a session.

## The limits of what changed, in security terms

**What stays exactly as it was:** the relay passes **opaque bytes**. The TLS handshake with certificate-fingerprint
pinning, the `AUTH1`/`AUTH2` authentication, the mux, and the egress policy all run **between the two machines,
inside** the relay's stream, without a single character changed. The server cannot read a URL, or content, or a domain
name.

**What changes:** the traffic **crosses** the server. So the product document's wording is amended from "browsing data
does not pass through the server" to the more precise and provable form: **"the server cannot read browsing data"**.

**Carried out on 2026-09-09:** success criterion 16 was restated as "**the server cannot read browsing data, and it
does not pass through the API container**", and is now measured in two parts — `docker stats` on `api` and `relay`
separately during a video, and the absence of any domain name from the relay's log. The detail is in
[`acceptance-checklist.md`](../acceptance-checklist.md), followed by item 7 in
[`security-review-server.md`](../security-review-server.md), which used to measure with `iftop` across the whole server
and so could not tell the two containers apart.

**What grows:** the trust boundary recorded in the plan (the server knows the session secret and both certificate
fingerprints, so a compromised server controlling the path could in theory intercept the tunnel) existed before this
decision, but the relay puts the server on the path **always** rather than as a possibility. The hardening deferred to
version two — a permanent key pair per device signing the session certificate's fingerprint — becomes the **highest
priority**, because it closes that door for good.

## Consequences

- **The client side is ready:** `RelayTransport` and `RelayProtocol` are built and tested (798 lines of tests including
  fuzzing of the preamble parser). Enabling it is one line: `TunnelSessionOptions.Transport = new RelayTransport(...)`.
  `SymmetricConnector`, `NerdbankMux` and `EgressPolicy` are unchanged.
- **What is missing is the service on the server:** verifying the signed token, pairing the two ends by `session_id`,
  pumping the bytes, and the limits. The plan's estimate: one week on track A.
- **The scale acquits the reservations:** the project serves **five users at most**. At that scale the reservations
  about the bandwidth bill (a few hundred gigabytes a month), about the efficiency of asyncio pumping in Python, and
  about the 500-control-channel ceiling all fall away. Any capacity reservation is reopened **by measurement** if the
  number of users changes, not by estimate.
- **The code-signing certificate is reconsidered:** it was on the critical path for wide distribution. For five known
  machines, getting past the SmartScreen warning once per machine is cheaper than a yearly subscription — a separate
  operational decision, which this ADR neither blocks nor forces.
- **A measurement is required before adoption:** the relay's throughput and latency on the chosen machine, by the same
  method used in `docs/performance-week5.md`. No number is adopted without measurement. **Partly carried out on
  2026-09-08:** [docs/performance-relay.md](../performance-relay.md) — throughput around 2 Gbit/s (nine times the
  fastest thing measured for the tunnel), added latency below a millisecond, and pairing at 0.6 ms, so the service is
  not the bottleneck. But **CPU and memory per session were not measured**: the measuring machine does not report
  them, and they are the substance of this reservation — it stays open until the benchmark is run on the deployment
  host.
