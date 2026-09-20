# The acceptance list: the first release's success criteria (18 items)

The items' source: section 16 of the product document.

> ## ⏸ Stopped by the product owner's decision — 2026-09-10
>
> The manual round is **stopped at this state**, not abandoned halfway. What is recorded below is true and dated, and
> what is empty is deliberately empty. To resume it later: the section
> [The manual round](#the-manual-round-one-session-closes-what-remains) carries the full steps, and
> [`scripts/acceptance-round.ps1`](../scripts/acceptance-round.ps1) gathers the evidence.
>
> **The state at the stop: 5 met · 6 partial · 6 untested · 1 that will not be implemented, by decision.**

**Last updated: 2026-09-10.** What is recorded below is taken from the application's logs on both machines and from a
real run between two different networks, not from automated tests. An item with no evidence stays **empty**: an
acceptance list half of which is guesswork is worse than an empty one.

## The environment the verification took place in

| Item | Value |
|---|---|
| The server | `rg.mohamedshaker.net` — API + relay on the same host (the relay on 8443) |
| Machine A | Windows 11 build 26200, ARM64 |
| Machine B | Windows 11 build 22621 |
| The two networks | Different (`same_public_ip=false`): the guest `196.129.149.33`, the host `154.178.228.43` |
| The build | `Josour 0.1.0`, built 2026-09-09 23:41 — it carries ADR-0010 and the disclosure fix; a self-contained unsigned exe |
| The reference session | `65f30eac-4b2c-4831-a4e2-d1d5ba0854ed` — 2026-09-10 from 10:45:51 to 10:47:17 |
| The transport | **The relay in all seven sessions** — the direct path never won |

> **The 2026-09-10 session is the reference.** It ran on the 23:41 build that carries "all sites through the host"
> ([ADR-0010](decisions/0010-route-all-through-host.md)) and the disclosure-text fix, and it carried **34 megabytes**
> in earnest. The sessions before it (2026-09-09) ran **with the list enforced**, and remain in the table wherever they
> add something the newer session does not prove — such as both directions of disconnection.

---

## The eighteen criteria

| # | Criterion | How it is verified | Result | Evidence |
|---|---|---|---|---|
| 1 | Installing the program on two Windows machines | Copying `Josour.exe` alone, **matching the SHA-256 hash**, then getting past SmartScreen once per machine | 🚫 **will not be implemented as written** ([ADR-0011](decisions/0011-no-code-signing-certificate.md)) | The criterion required a signed installer with no warning. **No certificate is bought** — 3 to 5 known users. The single file works on both machines after the packaging fix; **and it has not been tried on Windows 10** — both machines are 11 |
| 2 | Both parties signing in | A successful sign-in from both machines; a `devices` row for each | ✅ **yes** | 2026-09-09: `Signed in as host@… on device 9e1e873c…` on both machines |
| 3 | The host appearing as available | Enabling "available" → it appears at the user within two seconds with the reachability badge | ✅ **yes** | `Guest: 1 hosts listed` after `hosts.update`; it appears immediately in the log |
| 4 | Sending the connection request | The request window shows the name, the device, the duration, the browsing scope and the warning | ⚠️ **partial — a defect was fixed** | The request arrives, the window appears with the data, and a request was actually accepted on the fixed build. But reviewing the text revealed that it **had been showing a false sentence** ([the detail](#item-4-the-window-said-the-opposite-of-the-truth)); it was fixed and covered by six tests, **and it remains for the host to confirm they read "browsing scope" rather than "the company's list"** |
| 5 | Accepting or rejecting the request | A rejection returns a message; an acceptance creates a session; 60 seconds with no answer closes the window | ⚠️ **partial** | Acceptance is verified six times. **The rejection and the sixty-second timeout have not been tried** |
| 6 | Establishing a connection channel between the two machines | `session.connected` within 10 seconds; `winner_type` and `tls_version` recorded | ✅ **yes** | Seven sessions, all of them `winner="Relay"` and `tls=1.3`. The most recent **964 ms**; the range 465–2355 ms, all of it below 2.4 seconds |
| 7 | Launching the work browser | A Chrome window with an independent profile opens on the check page | ✅ **yes** | Three times, the last on 2026-09-10 10:46:12 — **10 milliseconds** between launching the browser and `is fully active` |
| 8 | The browsing passing through the host | The check page shows the host's IP; the actual browsing leaves from their address | ✅ **yes** | On the "all sites" build: **35,829,636 bytes down and 197,041 up** in 86 seconds. And the project owner's confirmation that the connection works well on both machines |
| 9 | The host's IP address appearing | The work browser shows the host's IP; the ordinary browser shows the user's IP | ⚠️ **partial** | The first half is verified. **The second half** (the ordinary browser showing the guest's address at the same moment) is undocumented |
| 10 | The user's other applications being unaffected | Teams/Outlook and the ordinary browser on the user's IP; no change to the system proxy | ⬜ untested | Guaranteed by design (the owning-PID check, and no touching of the system proxy) but **not verified by hand** |
| 11 | Disconnecting from either side | The host's disconnect button closes the user's browser, and the reverse | ✅ **yes** | **Both directions recorded**: `reason=host_ended` twice and `reason=guest_ended` twice |
| 12 | The session ending automatically when its duration runs out | A 15-minute session ends with `expired` ± 5 seconds and the browser closes | ⬜ untested | No `reason=expired` in any log; every session was ended by hand or by a failure |
| 13 | Restoring the browser's settings after termination | No proxy in the system settings; ordinary Chrome with no "did not shut down correctly" | ⬜ untested | — |
| 14 | Recording the session's basic data | A complete `sessions` row: the times, the reason, the bytes; and `session_domains` when `log_domains` is on | ⚠️ **partial** | The client sent `session.end` with real numbers: `bytes_up=197041 bytes_down=35829636 domains=0` (and `domains=0` is correct because `log_domains=false`). **The database rows were not inspected directly** |
| 15 | Stopping the user reaching the host's machine or network | From the work browser: `192.168.1.1`, `localhost`, `[::1]` and the host's public IP are all refused | ⬜ not tested by hand | Covered by 59 automated tests in `Josour.Egress.Tests` + `IpRangePolicy`, but **the manual check is required** |
| 16 | The server cannot read browsing data, and it does not pass through the API container | Two parts, during a video in the work browser: `docker stats api` stays at the heartbeat level, and `docker stats relay` rises with **opaque** traffic — bytes with not one domain name in the relay's log | ⬜ untested | The wording was adopted on 2026-09-09 ([ADR-0009](decisions/0009-relay-default.md)); the measurement has not been made yet |
| 17 | Native WebSocket persisting throughout | 8 hours; `presence.connected=true`; reconnection after a 30-second cut | ⚠️ **partial** | **Reconnection is verified**: on 2026-09-10 at 11:01:15 the channel dropped, and at 11:01:18 it came back — **2.7 seconds, one attempt** — and during it the network address changed **and** the server answered 4401, so the token was refreshed automatically. **The eight hours were not tested** (the longest documented run is about two hours) |
| 18 | The system working on a small VPS | Production on 1 vCPU / 2 GB; CPU < 20% and RAM < 60% with 20 clients | ⚠️ **partial** | It actually runs on a VPS of that specification with API + relay + PostgreSQL. **Not measured under 20 clients** |

**The tally: 5 met · 6 partial · 6 untested · 1 that will not be implemented, by decision
([ADR-0011](decisions/0011-no-code-signing-certificate.md)).**

And two items could not have passed as written, so they were dealt with before the round: **item 16** was reworded and
the wording adopted, and **item 4**'s window was showing a false sentence, which was fixed. The detail follows the
table.

---

## Item 4: the window said the opposite of the truth

It was found by reading the code **before** the round began, not by running it. The request window on the host showed a
fixed sentence under the heading:

> "Only the sites on the company's list can be reached through your machine."

And directly under it, in the info bar, the correct sentence:

> "They will be able to browse any site over your connection, not a specific list."

The host reads **two contradictory claims in one window, and the false one is the reassuring one** — and they read them
in the sixty seconds in which they decide. That is a breach of item 4 in its substance rather than its wording:
disclosure before consent is the product itself here.

**The cause:** the sentence and the heading ("The allowed sites") were static text in XAML, so they did not follow
[ADR-0010](decisions/0010-route-all-through-host.md) when everything else did. The varying disclosure changed; the
fixed one went on describing a world that had ended.

**The fix:** the heading and the sentence now follow the list's actual state — "browsing scope" with no list sentence
when there is no list, and "the allowed sites" with its sentence when there is one. And the decision left the WPF window
for `DisclosureText` in a tested layer, because the window is **the one place in the application with no test project**
— the same debt that produced two of week seven's failures.

**Six tests** pin the three cases apart: "no list", "a list that could not be loaded", and "a list that blocks
everything" — three different consents, so three different sentences.

## Item 16: it was reworded (adopted 2026-09-09)

The item was written when the server passed no browsing data at all. After
[ADR-0009](decisions/0009-relay-default.md) **the bytes pass through the relay service** — which is on your server
today — so the old text promised what the product no longer does.

**The old text:** "Browsing data does not pass through FastAPI".
**The adopted text:** "**The server cannot read browsing data, and it does not pass through the API container**".

The new wording is narrower in one part and wider in another: it gives up the claim "it does not pass", and commits to
something stronger and harder — **the inability to read** — which is a provable commitment rather than a description of
a route.

### Why the claim stays true

The TLS handshake, the certificate pinning and `AUTH1`/`AUTH2` run **between the two machines, inside** the relay's
stream. The relay pumps the bytes and does not decrypt them: it holds no key, and sees not one domain name. What the
server's operator sees is ciphertext, traffic sizes and timings.

### How it is measured — two parts, during a video in the work browser

| Part | Command | Expected |
|---|---|---|
| It does not pass through the API container | `docker stats --no-stream api` before and during | `NET I/O` steady at the level of WebSocket heartbeats |
| It passes through the relay, opaquely | `docker stats --no-stream relay` before and during | `NET I/O` rises with the video's size |
| And the opacity itself | `docker compose logs relay \| grep -iE '[a-z]+\.[a-z]{2,}'` | Session ids and sizes only, **with not one domain name** |

The first two rows together are the proof: a rise in `relay` **without** a rise in `api` proves the separation, and the
third proves the opacity.

### What changed as a consequence

- [`security-review-server.md`](security-review-server.md) item 7 — it used to measure with `iftop` across the whole
  server, which cannot tell the two containers apart, so it became per-container.
- [`Josour-MVP-Implementation-Plan.md`](Josour-MVP-Implementation-Plan.md) — the manual E2E test row.

---

## The manual round: one session closes what remains

The tool: [`scripts/acceptance-round.ps1`](../scripts/acceptance-round.ps1), with three phases around **one session**,
each appending to an evidence file in `%LOCALAPPDATA%\Josour\acceptance\`.

**The shortest duration the interface offers is 15 minutes** (the options are 15, 30, 60 and 120), so one session is
enough for the whole round: the `during` checks are made in its first minutes, and then it is left to end on its own,
which closes item 12.

### Before the session — on both machines

```powershell
powershell -ExecutionPolicy Bypass -File scripts\acceptance-round.ps1 -Phase before
```

It captures the machine's intangible state: the system proxy settings, your public address, and the record of previous
sessions.

### During the session

**On the host, before pressing any button** — items 4 and 5:
1. Read the window: does it say "**browsing scope**" and "they will be able to browse any site over your connection"?
   And no sentence about "the company's list" may appear.
2. **Reject** the first request. Then leave a second request unanswered for **60 seconds** until the window closes
   itself.
3. Accept the third request with a duration of **15 minutes**.

**On the guest** — items 9, 10 and 15:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\acceptance-round.ps1 -Phase during
```
Then, in the **work browser**, five tabs: `https://api.ipify.org` (the host's address must appear), then
`http://192.168.1.1/`, `http://localhost/`, `http://[::1]/` and the host's public address — **all four are refused**.

And open Teams or Outlook and confirm they work.

> **The one screenshot that closes item 9:** the work browser and your ordinary browser side by side, both on
> `api.ipify.org`, **with two different numbers**. Two numbers in one picture are stronger than any line in a log.

### After the duration ends on its own

```powershell
powershell -ExecutionPolicy Bypass -File scripts\acceptance-round.ps1 -Phase after
```

It reads the end reason from the log (item 12 closes with `reason=expired`), and checks that the work browser exited
cleanly (item 13). It remains for you to open the proxy settings in Windows and see them empty.

### What the tool does and the eye cannot

A deliberate connection to the proxy's port **must be refused**: the proxy accepts only the work browser's process
tree. The refusal here is not a defect but **item 10 succeeding**, and the tool records it as an explicit verdict.

## Additional security checks (from section 14 of the document)

| Check | Method | Result |
|---|---|---|
| The listener accepts nothing but TLS and cuts off without authentication | `nmap -sV` on the host's port during the connect window | ⬜ untested — **and note**: with the relay no listener is opened at all, so the check applies to the direct path alone |
| Deleting the session keys | `select count(*) from session_keys` = 0 after termination | ⬜ not checked on this deployment |
| No browsing content in the logs | grep in the server's and the client's logs: no URLs and no headers | ⚠️ The client's logs were checked repeatedly during diagnosis and carried no link; the server's logs were not checked |
| An enterprise proxy policy | Enabling `ProxySettings` through the registry → the session ends with `browser_not_proxied` | ⬜ untested |
| Account lockout and rate limiting | 10 wrong attempts → 423; exceeding the limit → 429 | ⚠️ Observed in practice: four wrong sign-in attempts returned `401 invalid_credentials` as designed |
| Central termination | `POST /admin/sessions/{id}/terminate` closes the tunnel and the browser within 5 seconds | ⬜ untested |
| **The relay token appears in no diagnostics and no log** | grep for the token in the client's log | ✅ Covered by an automated test (`TheRelayTokenNeverReachesTheDiagnostics`) and the diagnostic logs were free of it |

---

## Vulnerabilities closed (regressions that must stay covered)

### Week four — all covered by automated tests

| The vulnerability | The manual check | Result |
|---|---|---|
| Header injection in the proxy | Send a request containing `X-Test: a\nHost: evil.example` | ⬜ |
| SSRF from the user's side | A name that resolves to 127.0.0.1 through the proxy | ⬜ |
| Name normalisation failing open | Names with control characters in a CONNECT request | ⬜ |
| The deprecated IPv4 wrapping `::a.b.c.d` | A name that resolves to `::127.0.0.1` | ⬜ |

### Week seven — four field failures

| The failure | The manual check | Result |
|---|---|---|
| The relay's address being a hostname the transport refused | A session over a relay given by name | ✅ Six sessions, all `winner="Relay"` |
| The Arabic build crashing on the product's name in `User-Agent` | Run with `--lang ar` and confirm there is no `[FTL]` | ✅ Verified at build time |
| A fast clock disabling the accept and reject buttons | Set the host's clock two minutes fast and send a request: both buttons must stay enabled | ⬜ **worth checking** — the fix is covered by six tests but has not been tried with a real clock |
| The proxy refusing every connection from the work browser | A session in which the check page arrives | ✅ `reached the probe page` twice |

### Auto-accepting a trusted guest ([ADR-0012](decisions/0012-auto-accept-trusted-guests.md))

The feature adds a path in which **no request window is shown**, which is to say it touches items 4 and 5 in their
substance. All the logic is in `AutoAcceptPolicy` and is covered by twenty tests, but the following needs two real
machines and has not been tried:

| The manual check | What should happen | Result |
|---|---|---|
| The switch off (the shipped state) and a request from a guest in the list | The window is shown as usual | ⬜ |
| Accepting with "accept from this guest automatically" and then a second request from them | The second request is accepted with no window, and an "automatic connection" notification appears | ⬜ |
| A request longer than the rule's ceiling | The window is shown | ⬜ |
| The guest reinstalling the application (a new device) and then requesting | The window is shown | ⬜ |
| Deleting the guest from the settings and then a request from them | The window is shown | ⬜ |
| Switching the master switch off and closing the settings window **without saving**, then a request | The window is shown — switching off applies at once, not on save | ⬜ |
| After an automatic acceptance: the security log on the server | A `request_auto_accepted` row attributed to the host with the guest's identity in it | ⬜ |
| Ending the session from the automatic-acceptance notification | The session ends at once | ⬜ |

### The interface on Avalonia ([ADR-0013](decisions/0013-avalonia-and-macos.md))

The port changed every screen in the application, and **headless interface tests cover what loads and binds, not what
looks right to the eye**. This round needs a human who looks:

| The manual check | What should happen | Result |
|---|---|---|
| Opening every window on Windows in Arabic | The layout is a correct mirror, the digits are Western, and technical values (an address, an email, a version) stay on the left | ⬜ |
| Opening every window on macOS in Arabic and in English | As above | ⬜ |
| **Bold Arabic text on macOS** (headings, tabs, info-bar titles) | Arabic letters rather than empty boxes — see [macos-port.md](macos-port.md#fonts-on-macos--a-failure-worth-documenting). **No automated test covers this; the eye alone** | ⬜ |
| The tray icon on macOS | It appears in the menu bar, its menu is translated and mirrored, and both toggles work | ⬜ |
| Closing the main window | The application stays in the bar and does not exit | ⬜ |
| An incoming-request notification on Windows | It carries "Accept" and "Reject" and they work | ⬜ |
| An incoming-request notification on macOS | It appears **with no buttons**, and the window is the path to answering | ⬜ |
| "Start on login" on macOS | `~/Library/LaunchAgents/com.josour.client.plist` is written, and the application starts minimised after signing back in | ⬜ |
| A second instance on macOS | It exits at once and brings up the first one's window | ⬜ |

### The guest role on macOS

| The manual check | What should happen | Result |
|---|---|---|
| A session from a Mac guest to a Windows host | The sites see the host's address | ⬜ |
| A session from a Windows guest to a Mac host | As above | ⬜ |
| **Criterion 10 on a Mac**: open Safari and Teams during a session | They stay on your own connection, and the `rejected_by_owner` counter rises if one of them tries the proxy | ⬜ |
| Ending the session | The work browser and all its children close; `ps` shows no remnants | ⬜ |
