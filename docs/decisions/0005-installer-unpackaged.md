# ADR-0005: a signed unpackaged installer (Inno Setup) instead of MSIX

**Status:** accepted, and **amended by [ADR-0011](0011-no-code-signing-certificate.md) on 2026-09-10**: the signing
requirement was dropped, and the installer left the critical path.

## Context
The product document allows "MSIX or a signed installer". The application runs Chrome/Edge as a child process and
needs a Windows Firewall rule for its listener.

## Decision
A digitally signed Inno Setup installer. The elevated installer adds a firewall rule for the executable.
Auto-update (Velopack) waits for the second release.

## Reasons
- Child processes of an MSIX application inherit the package identity and its file virtualisation, which moves the
  browser's profile path and complicates Chrome's environment.
- MSIX cannot add firewall rules during installation.

## Consequences
- Request the code-signing certificate on day one (issuance takes days).
- Revisit MSIX later, after testing how egress behaves from inside a package identity.
