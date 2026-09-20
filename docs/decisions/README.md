# Design decisions (ADRs)

| Number | Decision | Status |
|---|---|---|
| [0001](0001-ui-framework-wpf.md) | Client interface: WPF on .NET 8 | **Superseded by 0013** |
| [0002](0002-tls-version-policy.md) | TLS 1.3 where available, 1.2 as the floor | Accepted 2026-09-03 |
| [0003](0003-nat-traversal-and-relay-gate.md) | Symmetric connection with candidates + a relay decision gate | Accepted 2026-09-03; the gate was closed by 0009 |
| [0004](0004-non-allowlisted-routing.md) | Non-allow-listed sites go through the user's own connection | **Superseded by 0010** |
| [0005](0005-installer-unpackaged.md) | A signed unpackaged installer instead of MSIX | Accepted |
| [0006](0006-multiplexing-library.md) | Multiplexing library | Accepted 2026-09-04 (Nerdbank.Streams), with the window correction in week 5 |
| [0007](0007-argon2-parameters.md) | argon2id parameters against the VPS ceiling | Accepted 2026-09-05 (option B: m=19 MiB, t=2, p=1) |
| [0008](0008-rate-limit-policy.md) | Rate limiting and the automatic response to abuse | Accepted 2026-09-05 (closes reservations 3.1 and 3.2) |
| [0009](0009-relay-default.md) | The relay as the default transport, direct as an opportunistic upgrade | Accepted 2026-09-07 (closes the 0003 gate) |
| [0010](0010-route-all-through-host.md) | Everything the work browser asks for goes through the host | Accepted 2026-09-07 (supersedes 0004) |
| [0011](0011-no-code-signing-certificate.md) | No code-signing certificate; one file, distributed by hash | Accepted 2026-09-10 (amends 0005) |
| [0012](0012-auto-accept-trusted-guests.md) | Accepting guests the host has already decided about | Proposed 2026-09-18 |
| [0013](0013-avalonia-and-macos.md) | One Avalonia interface, and macOS as a supported system | Proposed 2026-09-18 (supersedes 0001) |
