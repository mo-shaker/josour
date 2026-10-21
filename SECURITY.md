# Reporting a security vulnerability

**Do not open a public issue for a security vulnerability.** This project routes real people's browsing through
other people's home addresses, and a flaw published before it is understood can be used before the people running
it know it exists.

Send the details to: **me@mohamedshaker.com**

Put `Josour security` in the subject. What helps: the version or commit, the operating system, and enough steps to
reproduce what you saw.

## What to expect

One person maintains this in their own time, and it **offers no support and guarantees no response time**. I will
read what you send, and I will fix what I can, and sometimes I will not. The licence ([Apache-2.0](LICENSE))
distributes the software "as is" with no warranty, and that is not a formality — it is an honest description of
what you are getting.

If what you found is serious and I have not responded, **publish it**. Warning people matters more than waiting
for me.

## What I care about most

The boundaries the whole product rests on, worst-first by what breaking them would cost:

| Boundary | Where it lives | Why it matters |
|---|---|---|
| Refusing private addresses after name resolution | `IpRangePolicy`, step 6 in [docs/protocol.md](docs/protocol.md) | It alone keeps the guest away from the host's local network, their router's admin page and their `localhost` services |
| Putting the guest's system proxy back | `SystemProxyGuard`, the guardian process, [ADR-0015](docs/decisions/0015-system-proxy-instead-of-work-browser.md) | During a session every application on the guest's computer that follows the system proxy leaves from the host's address; after it, nothing may. A session that ends — cleanly, by a crash, by a power cut — must leave the settings exactly as they were |
| Nothing leaves directly by accident | `ProxyRouter`, `ConnectProxyServer` | A port outside `allowed_ports`, an address literal or a failed open is refused with 403; with every site allowed, nothing the proxy receives may go out from the guest's own address |
| The host's consent | [ADR-0012](docs/decisions/0012-auto-accept-trusted-guests.md), section 5a in [docs/ws-protocol.md](docs/ws-protocol.md) | Any path that creates a session without consent — or makes a trust rule apply to somebody it was not written about — breaks the product rather than having a bug in it |
| The tunnel secret and the relay token | `session_keys`, `relay_tokens` | End-to-end encryption rests on them, and the server is not supposed to be able to read anything |
| The local proxy stays local | `ConnectProxyServer` (loopback only), `SameUserAdmission` | It is the door to the host's address: only this machine, and on Windows only the signed-in account's processes |

## What differs between the two systems

Windows changes the current user's WinINet settings with `InternetSetOptionW` and needs no administrator rights; a
Group Policy that keeps proxy settings per machine (`ProxySettingsPerUser=0`) makes the guest role unavailable rather
than half-applied. macOS changes every enabled network service with `networksetup`, and when macOS asks for
authorisation it shows its own password dialog — Josour never holds a password. On Windows the local proxy admits only
processes of the same Windows account (the owning process from `GetExtendedTcpTable`, its account from its token); on
macOS the loopback listener is the boundary. If you find a way to make the settings stay pointed at a dead proxy after a
session, or to make the local proxy send something out from the guest's own address, that is what I want to hear about
more than anything else.
