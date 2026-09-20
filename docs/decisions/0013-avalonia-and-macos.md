# ADR-0013: one shared interface on Avalonia, and macOS as a supported system

**Status:** proposed on 2026-09-18; **the port is implemented**: every screen, both roles, and distribution as a
`.app` bundle — [macos-port.md](../macos-port.md). What remains before it closes: a real session between a Mac and a
Windows machine, and the signing decision. **Supersedes [ADR-0001](0001-ui-framework-wpf.md)** (WPF on .NET 8), which
was still "proposed".

## Context

The product was asked to work on macOS in both roles: as a host sharing their connection, and as a guest borrowing
one. The interface today is WPF, and WPF only runs on Windows. In practice the question is two questions rather than
one, and they are often conflated:

1. **The interface**: with which framework are the windows drawn on both systems?
2. **The platform seams**: where do secret storage, launching the work browser, the connection-owner check, the
   firewall and start-on-login come from?

All the libraries (`Core`, `Tunnel`, `Proxy`, `Egress`, `Browser`, `Infrastructure`) target plain `net8.0`, and
`Josour.App` alone is `net8.0-windows`. ADR-0001 itself wrote that moving later is possible "because all the logic is
in libraries independent of the interface" — and that assumption is being tested now for the first time, and it held:
the tunnel, egress and proxy layers contain not one Windows-specific call.

## Decision

**Avalonia as one interface for both systems**, not two interfaces.

1. `Josour.App` moves from WPF to Avalonia and targets `net8.0` with no platform suffix.
2. The `ViewModels` stay as they are: they are `CommunityToolkit.Mvvm` today and know nothing about WPF. The port
   touches `Views`, `App.xaml` and the platform-specific service layer.
3. **The platform seams become interfaces with an implementation per system**, not conditions inside
   `if (OperatingSystem.IsWindows())`.
4. `Strings`, `LocalizedStrings` and the resx files do not change: they are already framework-independent.

## Reasons

**Why not two native interfaces?** Because every feature after that is written twice. The last feature
([ADR-0012](0012-auto-accept-trusted-guests.md)) touched four screens; with two interfaces it would have touched eight,
half of them untestable on the developer's machine. And the worst thing about two interfaces is not the cost but the
silent divergence: the disclosure screen before acceptance **is the consent itself**, and two copies of it that drift
apart over time mean two hosts who agreed to two different things.

**Why Avalonia and not MAUI?** XAML and MVVM are what is already written, and Avalonia is the closer of the two to WPF
in both text and concepts, so the port is a translation rather than a rewrite. MAUI on the desktop goes through Mac
Catalyst and has a weaker story for menu-bar icons and multiple windows, and both are central here: the application
lives in the bar most of the time.

**Why not settle for a command-line tool on the Mac?** `Josour.Spike` runs the real stack and would nearly serve as a
host today. But the product's audience is non-technical by definition, and the consent screen on a Mac may not be a
command line. The tool stays useful as a first verification step and for a permanent host run by someone technical.

**Why explicit seams instead of conditions?** Because `if (!OperatingSystem.IsWindows()) return null;` produces silent
behaviour: `PermissiveOwnerPidChecker` means "I do not know who owns this connection", and on Windows that is a
fallback, while on a Mac it would be the permanent state — that is, a security control falling away without anyone
saying so. An explicit interface forces every system to have an answer, or to declare that it has none.

## Consequences

- **ADR-0001 is marked superseded.** It was never accepted, and its reason (a tray icon and notifications with buttons
  outside MSIX) stays true on Windows but no longer settles the choice on its own.
- **`H.NotifyIcon.Wpf` is out** in favour of Avalonia's `TrayIcon`. `Microsoft.Toolkit.Uwp.Notifications`, however,
  **stayed**: the project now targets two frameworks, `net8.0` and `net8.0-windows10.0.19041.0`, because dropping the
  package would have taken the "Accept" and "Reject" buttons out of the Windows notification — a capability the port
  was not asked to spend. The macOS notification **carries no buttons** (`osascript display notification`; buttons need
  a signed, bundled application), so the request window is the primary path there — and it is a path that already
  existed, because Focus Assist on Windows would drop notifications
  ([`IncomingRequestPresenter`](../../client/src/Josour.App/Services/IncomingRequestPresenter.cs)).
- **The interface has tests, for the first time.** Avalonia's headless platform loads the same XAML and applies the
  same themes with no screen, so it became possible to test the screen on which consent is taken — the screen nothing
  checked under WPF.
- **The eleven WPF-UI controls** became three controls written by hand (`Icon`, `Card`, `InfoBar`) and the rest
  Avalonia's own. The icons are from the same family (Fluent System Icons, MIT), embedded as geometry rather than as a
  font or a package.
- **The custom title bar and the Mica background are gone**: the windows use the system's own title bar, which is the
  right thing on macOS anyway.
- **[ADR-0011](0011-no-code-signing-certificate.md) gets harder on a Mac.** Gatekeeper is stricter than SmartScreen: an
  unsigned, unnotarised application needs an explicit manual step from the user. Mac distribution needs a separate
  decision; this ADR does not settle it.
- **Secrets on a Mac live in a file with `0600` permissions**, not in the Keychain. The Keychain was tried first and
  fell for a reason not accounted for here: the Keychain binds every item to the code-signing identity of the
  application that created it, and [ADR-0011](0011-no-code-signing-certificate.md) says "no signing" — so every build
  is a different application as far as it is concerned, and every update would have left users locked out of their own
  secrets with `errSecInvalidOwnerEdit`. The detail, and what the alternative costs, is in
  [`FileSecretStore`](../../client/src/Josour.Infrastructure/Security/FileSecretStore.cs), and the choice is made in
  [`SecretStores.ForCurrentPlatform`](../../client/src/Josour.Infrastructure/Security/SecretStores.cs) so that no call
  site keeps its own opinion about where a secret lives.
- **The connection-owner check was implemented differently from what this ADR imagined.** There is no Job Object on a
  Mac, and what was proposed here was a process group (`setpgid`) and `libproc`. What was actually implemented: `lsof`
  for the socket's owner — because `libproc` means unpacking `socket_fdinfo` by hand for a security control that may
  not fail quietly — and **the process tree** for ownership, because a process group requires `setpgid` between `fork`
  and `exec`, which `Process.Start` does not allow. The detail and the measurements are in
  [macos-port.md](../macos-port.md).
- **The implementation table is in [macos-port.md](../macos-port.md)**, and this ADR does not close before a complete
  session passes between a Mac and a Windows machine in both roles.
