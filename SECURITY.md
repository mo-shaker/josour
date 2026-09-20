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
| The connection-owner check | `OwnerPidChecker` | It alone keeps the product from becoming a device-wide VPN on the guest's machine, sending **everything** out through the host's address |
| The host's consent | [ADR-0012](docs/decisions/0012-auto-accept-trusted-guests.md), section 5a in [docs/ws-protocol.md](docs/ws-protocol.md) | Any path that creates a session without consent — or makes a trust rule apply to somebody it was not written about — breaks the product rather than having a bug in it |
| The tunnel secret and the relay token | `session_keys`, `relay_tokens` | End-to-end encryption rests on them, and the server is not supposed to be able to read anything |
| Work-browser isolation | `Josour.Browser` | The machine's other applications must never pass through here |

## What differs between the two systems

The connection-owner check is implemented on both, by two mechanisms: `GetExtendedTcpTable` on Windows and `lsof`
on macOS, because there is no public call there that maps a socket to a process. Browser ownership comes from a Job
Object on Windows and from the process tree on macOS. If you find a way to make either of them admit a connection
the work browser did not open, that is what I want to hear about more than anything else.
