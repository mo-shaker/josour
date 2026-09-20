# ADR-0010: all work-browser traffic goes through the host

**Status:** **accepted by the product owner on 2026-09-07.** **Supersedes
[ADR-0004](0004-non-allowlisted-routing.md)** and amends the product document (section 6.6).

## Context

The product owner stated the product's primary purpose: **opening sites that only work from inside the host's
country** — a user in Egypt browsing with the address of a host in Saudi Arabia, and "the project has no point
without it".

The ADR-0004 model contradicts that purpose in practice: only listed sites go through the host, and everything else
leaves from the user's own connection. So any unlisted site leaves from Egypt and is blocked. And even in good faith,
listing by hand is unworkable: **a single page pulls its resources from dozens of subdomains** (delivery networks,
media, analytics, fonts, APIs), so the user would have to discover every domain and add it — which is exactly the
technical step the product exists to remove for an audience with no technical background.

## Decision

1. **Everything the work browser asks for goes through the host.** The routing decision is no longer conditional on
   matching the list.
2. **The list becomes an optional control** an administrator owns, to restrict the destinations of a particular
   session, and it is off by default. The `AllowlistMatcher` mechanism and its numbered versions stay as they are;
   what changes is that it is no longer the condition for passage.
3. **The local proxy no longer opens a direct connection** in the default mode. The direct path was what implemented
   ADR-0004, and it fell with it.

## What does not change — and this is the most important paragraph in this ADR

"All sites" means **lifting the third condition only** of the eight egress-policy steps in section 6 of
`docs/protocol.md`. Everything else stays in force, literally:

| Step | State after this decision |
|---|---|
| 1. Refusing an address literal (`ip_literal`) | **Kept** |
| 2. Normalising the name with STD3 | **Kept** |
| 3. Matching the list (`not_allowed`) | **Lifted by default** ← the subject of this decision |
| 4. The port is within `allowed_ports` (80, 443) | **Kept** |
| 5. Resolving DNS once, with a timeout | **Kept** |
| 6. **Refusing any resulting blocked address (`private_ip`)** | **Kept — not negotiable** |
| 7. Connecting to the checked list, not to the name | **Kept** |
| 8. The limits: 50 opens/second, the stream ceiling | **Kept** |

Step 6 is the real security boundary: it is what keeps the guest away from the host's local network, their router's
page, their `localhost` services and their public address. **"Pass everything" does not in any way mean disabling
that guard**, and acceptance criterion 15 stays as it is and must stay green.

Likewise **the scope of enforcement is unaffected:** the proxy serves **the work browser alone** (the owning-PID check
inside a Job Object). So Teams, Outlook and the ordinary browser stay on the user's own connection with their own
address. Acceptance criterion 10 still stands, and the product does not become a device-wide VPN.

## Consequences

- **An unintended gain: DNS resolution happens at the host.** That is what makes content delivery networks return
  addresses near the host, so the geographic effect is complete rather than limited to the exit address. Under
  ADR-0004, unlisted domains were resolved locally in Egypt.
- **The host's disclosure changes fundamentally.** The request window used to show **the actual list of sites** for the
  requested version (a requirement of section 15 of the product document). That wording is no longer truthful: the
  window must say plainly that the guest will be able to browse **any site** over the host's connection and with their
  address. This is **a real widening of the host's responsibility** and consent must be built on it. Updating
  `AllowlistDisclosure` and the request screen is a condition for closing this ADR.
- **The host's and the relay's bandwidth use rises** from "some domains" to "all browsing". Acceptable at five users
  ([ADR-0009](0009-relay-default.md)), and re-evaluated by measurement if the number changes.
- **`session_domains` becomes far more consequential for privacy.** It used to record domains listed in advance and
  known to both parties; it can now record the guest's **entire browsing history**. `log_domains` stays off by default
  and must stay that way, and enabling it needs a disclosure to the guest, not to the host alone.
- **ADR-0004 is marked superseded**, and with it the "block instead of pass" option deferred to version two falls away,
  because there is no longer anything for it to apply to.
