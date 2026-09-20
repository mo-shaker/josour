# ADR-0002: the TLS version policy for the channel between the two machines

**Status:** accepted by the product owner on 2026-09-03. Amends the product document (sections 10 and 13, "TLS
1.3").

## Context
`SslStream` on Windows goes through Schannel. Windows 10 does not support TLS 1.3, and asking for
`SslProtocols.Tls13` explicitly throws `Win32Exception 0x80090304`. `CipherSuitesPolicy` is not supported on
Windows.

## Decision
- `SslProtocols.None` on both ends (the system default).
- After the handshake: refuse any connection below TLS 1.2.
- Record the negotiated version in `sessions.tls_version`, so the real Windows 10/11 distribution is known rather
  than assumed.

## Consequences
- Windows 11 negotiates TLS 1.3 on its own; Windows 10 works over TLS 1.2 with AEAD.
- The requirement, restated: "TLS 1.3 where the system supports it, and TLS 1.2 as the floor."
