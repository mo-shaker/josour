# ADR-0003: NAT traversal by symmetric connection, and a relay decision gate

**Status:** accepted 2026-09-03. **The decision gate (item 4) was closed by
[ADR-0009](0009-relay-default.md) on 2026-09-07:** measurement on a real pair proved a direct path impossible
between two ends behind CGNAT, so the relay became the default transport and direct became an opportunistic
upgrade. Items 1, 3 and 5 still stand.

## Context
The product document asks for a direct connection and defers a relay unless testing proves direct fails. The
intended audience — companies and distributed teams — usually sits behind firewalls and CGNAT, where UPnP is close
to absent. The prior estimate for direct success was 45% to 65%.

## Decision
1. **Symmetric connection:** both ends open a listener and gather candidates (`lan` when the public IPs match,
   `v6`, `upnp` through Mono.Nat, `public`) and dial the other's candidates in parallel. The first connection that
   passes authentication at the host wins.
2. **No TCP hole punching** in the first release (a week of work, it fails on corporate networks, and the real
   return arrives later with UDP/QUIC).
3. **A reachability probe from the server** when "available" is switched on, feeding a badge in the host list and
   the data behind this decision.
4. **The decision gate:** after at least 10 real pairs on users' own networks during weeks 1 and 2; if the
   connection rate within 10 seconds is below 85%, build a simple relay (it passes opaque bytes; TLS stays between
   the two machines) during weeks 5 and 6.
5. Transport sits behind `ITunnelTransport` from day one, so `RelayTransport` is an addition rather than a change.

## Consequences
- `Open.NAT` is rejected (abandoned since 2016); `Mono.Nat` 3.x replaces it.
- LAN candidates are only sent when the public IPs match, so the host's network topology is not disclosed for
  nothing.
