# Porting Josour to macOS

The decision is in [ADR-0013](decisions/0013-avalonia-and-macos.md): one interface on Avalonia, and macOS in both
roles — host and guest.

This document is an inventory of what is actually needed, taken from the code rather than from expectation, and the
state of each item.

## What works on a Mac today with no change

The whole solution was built on macOS (`dotnet build Josour.sln`) and its tests passed. The following libraries
contain not one Windows-specific call:

| Library | Role | Note |
|---|---|---|
| `Josour.Core` | The types and policies | `FirewallDiagnostics` alone needs a Mac branch |
| `Josour.Tunnel` | The tunnel, TLS, the candidates, the mux | Sockets and `Mono.Nat` — portable |
| `Josour.Egress` | **The host's side**: egress to the internet | **Entirely portable** |
| `Josour.Proxy` | **The guest's side**: the local proxy | `OwnerPidChecker` alone needs a Mac |
| `Josour.Infrastructure` | The server, the channel, the session, the settings | Secrets and device information |

The practical effect: **the host role is far closer to ready than the guest role**, because `Egress` — which is
everything the host does with the bytes — needs nothing.

## The seams

| # | Seam | On Windows | On macOS | State |
|---|---|---|---|---|
| 1 | Secret storage | DPAPI (`DpapiSecretStore`) | A file with `0600` permissions (`FileSecretStore`) — the Keychain was tried first and fell, see [ADR-0013](decisions/0013-avalonia-and-macos.md) | ✅ (`FileSecretStore`, `SecretStores`) |
| 2 | Device information | Registry keys | `sw_vers` | ✅ (`DeviceInfoProvider.ReadMacVersion`) |
| 3 | Locating the browser's path | The registry | `/Applications` and `~/Applications` (`MacBrowserLocator`) | ✅ |
| 4 | Launching the work browser | `--proxy-server` + an isolated profile | The same arguments; executing directly inside the bundle rather than `open` (`MacBrowserSession`) | ✅ |
| 5 | Containing the browser | A Job Object | The process tree: SIGTERM to the tree then SIGKILL (`MacProcessTree`) | ✅ |
| 6 | **The connection-owner check** | `iphlpapi!GetExtendedTcpTable` | `lsof` (`MacOwnerPidChecker`) + the process tree | ✅ **implemented and tested both ways** |
| 7 | Firewall diagnostics | `netsh advfirewall` | `socketfilterfw` | ✅ (`MacFirewallDiagnostics`) |
| 8 | Start on login | The `Run` key | A `LaunchAgent` in `~/Library/LaunchAgents` | ✅ (`StartupRegistration`) |
| 9 | The tray icon | `H.NotifyIcon.Wpf` | Avalonia's `TrayIcon` (the menu bar) | ✅ (`TrayService`) |
| 10 | Notifications | `Microsoft.Toolkit.Uwp.Notifications` | `osascript display notification` — **with no buttons** | ✅ (`MacNotifier`) |
| 11 | The interface | WPF + WPF-UI | Avalonia | ✅ **every screen ported** |
| 12 | A single instance only | A named mutex + event | A lock file (`flock`) + a Unix socket | ✅ (`SingleInstanceGuard`) |
| 13 | Resuming after sleep | `SystemEvents.PowerModeChanged` | The clock's difference from a monotonic timer | ✅ (`SystemConnectivitySignals`) |
| 14 | The clipboard and sound | WPF | `TopLevel.Clipboard` and `afplay` | ✅ (`ClipboardService`, `AttentionSound`) |
| 15 | Distribution | One file ([ADR-0011](decisions/0011-no-code-signing-certificate.md)) | A `.app` inside a `.dmg` via [publish-app.sh](../scripts/publish-app.sh) | ✅ **for the build**; ⬜ signing and Gatekeeper |

## Seam 6: the connection-owner check — why it is the hardest

The local proxy serves **the work browser alone**; that is what keeps the product from becoming a device-wide VPN, and
it is acceptance criterion 10 and an explicit consequence in
[ADR-0010](decisions/0010-route-all-through-host.md). On Windows it is implemented by asking `iphlpapi` for the
socket's owner and then checking that it is inside the Job Object.

On macOS there is no Job Object. And what exists today is `PermissiveOwnerPidChecker` — which literally means "I do not
know", and which on Windows is a rare fallback and on a Mac would be **the permanent state**. That is, a naive port
drops this control silently.

### What was done now: refusing instead of passing silently

The default was `RejectUnknownOwner ?? OperatingSystem.IsWindows()` — that is, "refuse the unknown on Windows **and
accept it everywhere else**". And with `PermissiveOwnerPidChecker`, which is all that exists off Windows, that meant
the local proxy **accepted every process on the machine**. It was not a decision taken but the side effect of a
default, and it dropped acceptance criterion 10 without anyone saying anything.

The default is now **`true` on every system** (fail-closed). And whoever wants "accept everything" must say so
explicitly — as the in-process tests and the Spike tool do. The difference between a declared choice and a silent
default is the whole difference here.

Its effect on macOS is that the existing guard in `ConnectProxyServer` fires: the proxy refuses to be constructed at
all. And above it `GuestViewModel.IsGuestRoleSupported`, so the guest page shows the reason and the request button is
disabled — the user knows before they ask, rather than by an exception after a session has been created.

### How it was implemented, and why in this shape

**`lsof`, not `libproc`.** macOS has no public call binding a socket to a process; the only route through the system
is `proc_listpids` then `proc_pidfdinfo` on every descriptor of every process, unpacking the large,
version-sensitive `socket_fdinfo` structure — a structure laid out by hand for a security control that may not be
quietly wrong. And `lsof` does that same walk, ships with every macOS, and has a documented field format (`-F`). It is
what the project already does for platform facts (`netsh`, `socketfilterfw`, `sw_vers`).

**No caching.** Measured: 20 milliseconds per call, cheap enough to ask on every connection. And a cached table opens a
real hole: an ephemeral port the browser released may be picked up by another process inside the caching window, so an
old row accepts it. Twenty milliseconds is a smaller price than an answer that is sometimes wrong.

**Ownership from the process tree.** Chrome does not open sockets from the process that was launched but from a child
of it, so comparing the launched PID alone would have refused every connection the browser makes. And the tree is read
every time rather than once: a cached set, with process ids being reused, is a route to accepting a process nobody
launched.

**And the browser is launched from inside its bundle rather than with `open`:** `open` hands the request to an existing
instance and returns, so no process remains that we own, wait for or kill — and the owner check needs a process of our
own.

### What was exercised

| | |
|---|---|
| The positive case | Actually running the stack with the `Spike` tool: Chrome started, the check page arrived, and the counters read `accepted: 19, rejected_by_owner: 0` — the opposite of the historic `33/33` failure recorded in the code |
| The negative case | An integration test: the real proxy + the real `lsof` checker + a browser that owns nothing ⇒ `RejectedByOwner: 1, Tunneled: 0` |
| That the refusal is a decision, not an inability | A test proving the checker **names the test's own process** — a refusal caused by failing to identify anything would have looked identical, and would mean the control does not work |

**Not exercised:** a real mac ↔ Windows session in both roles. It needs two machines.

## The interface: what actually changed

`Josour.App` now targets `net8.0` and `net8.0-windows10.0.19041.0` together. The two targets are not a luxury: the
Windows notifications package (`Microsoft.Toolkit.Uwp.Notifications`) only resolves on a Windows target, and dropping
it would have taken the "Accept" and "Reject" buttons out of the Windows notification — a capability the port was not
asked to spend. Everything else, and every screen, is built once from the same source.

**What replaced WPF-UI.** The interface used eleven of its controls. Three were written by hand — `Icon`, `Card` and
`InfoBar` in `client/src/Josour.App/Controls/` — and the rest are Avalonia's own. And the icons stayed **from the same
family** (Fluent System Icons from Microsoft, MIT licence) but are embedded as geometry in `Controls/Icons.axaml`
instead of a package or a font: fourteen icons do not deserve a dependency, and an icon font is another native asset
that would have to survive single-file publishing on two systems.

**What was gained.** The interface never had a test project — and the request window, where the host's consent is
taken, was the one screen nothing checked. Avalonia's headless platform made it possible: `tests/Josour.App.Tests`
loads the same XAML, applies the same themes and runs the same bindings with no screen. Thirty-eight tests, among them
that every icon key actually resolves, that every severity in `InfoBar` has **a different icon and not merely a
different colour**, and that the trust checkbox in the request window starts **unchecked**.

**What was lost.** WPF-UI's custom title bar (`ExtendsContentIntoTitleBar` and the Mica background): the windows now
use the system's own bar, which looks right on macOS anyway.

## Fonts on macOS — a failure worth documenting

The interface was ported broken, passed 1459 sound tests, and was found by a human looking at the screen.

**The symptom:** headings, tabs and every bold string appear as **empty boxes**, while ordinary text beside them is
fine. It looks like a failure in particular strings, and it is a failure in a font.

**The cause, by measurement rather than by guesswork:**

1. `WithInterFont()` was called — the line Avalonia's template ships — and **Inter has not one Arabic character**. It
   was removed.
2. And after removing it the failure remained: Avalonia's default font on macOS is **`Helvetica`, which has no Arabic
   either**. Per-glyph fallback while drawing rescues the regular weight and cannot manage `SemiBold` — so the bold
   alone breaks.

**The fix:** an explicit `FontFallbacks` to **`Geeza Pro`** on macOS only (present on every Mac, in Regular and Bold —
the two weights the interface uses). Windows needs nothing: Segoe UI there has Arabic in every weight.

**And why there is no test preventing its return:** four approaches were tried and all failed for different reasons —
`FontManager.DefaultFontFamily` does not change with `WithInterFont` at all; checking glyph coverage passes because
per-glyph fallback draws the Arabic despite a font that does not have it; a pixel comparison under real headless Skia
passes because **the fallback succeeds there and fails in the real window** (measured: 4860 of ink against 2153 for the
boxes, that is, real text); and the font's name is set to `Inter` by `FluentTheme` whether it exists or not. The
failure lives in the macOS window's drawing path, and the headless environment does not take it.

**So the conclusion that remains:** this class of failure is found only by a human who opens the application and looks.
An explicit item was added to the [acceptance list](acceptance-checklist.md) for it.

## What was actually exercised on this machine

- Building both targets, and `dotnet test Josour.sln` green.
- Running the application on macOS 26.6.2 in Arabic and in English: the log confirms
  `Secrets are stored with file-0600`, and `Tray icon created`, and `sw_vers` gives
  `macOS 26.6.2 (build 25G83)`, and the first-run window opens.
- A second copy of the application exited at once with code 0 while the first kept running: the `flock` lock and the
  socket work.
- A Windows publish (`-f net8.0-windows10.0.19041.0 -r win-x64`) produces `Josour.exe`.

- A **self-contained single-file** publish for `osx-arm64`: 40 megabytes that run on a Mac with no .NET installed (the
  log confirms a complete startup), which is what
  [ADR-0011](decisions/0011-no-code-signing-certificate.md) means by "one file that is copied and hashed".

**Not exercised:** a real session between a Mac and a Windows machine — it needs two machines. And the Mac interface
**has not yet been seen by a human**: all the verification is headless tests and the startup log, because taking a
screenshot on this machine needs a permission that was not granted and would have captured the user's whole desktop.
The first thing to do is open the application and look at it.

## Building a deliverable copy

```bash
scripts/publish-app.sh --dmg              # for this machine
scripts/publish-app.sh --arch x64 --dmg   # for Intel machines
```

Three notes about the script, all of them the price of mistakes made while writing it:

- **No `PublishSingleFile`.** On Windows the flag exists because the deliverable is a single `.exe` that really is
  copied. Here the deliverable is **a bundle**, a directory the user drags as one icon, so the runtime and the native
  libraries live inside it where `dyld` expects them. The first version used the flag and kept only the executable, so
  it threw away `libSkiaSharp.dylib`: a bundle that assembles cleanly and dies before drawing a window.
- **The script runs what it built.** It checks that `libSkiaSharp` and `libHarfBuzzSharp` are present, then actually
  **executes** the file (`--uninstall-notifications` exits at once) and fails the build if it does not work. A
  file-count check catches none of this.
- **The icon is soft above 64 pixels**: its source is `josour.ico` at 64×64. A 1024×1024 file in `Assets` fixes it.

## Running the Mac build

The SDK here is in `~/.dotnet` and not on `PATH`, and the app host needs `DOTNET_ROOT` to find the runtime:

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd client && dotnet run --project src/Josour.App -f net8.0 -- --mock
```

`--mock` runs a simulated server inside the application, so no backend is needed. And a **self-contained** publish
(`--self-contained true`) carries the runtime with it, so it needs no `DOTNET_ROOT` on the user's machine.

## The proposed order

- ~~**Stage A — a Mac host.** Seams 1, 2 and 7.~~ **Done.**
- ~~**Stage C — the interface.** Seams 11, 8, 9 and 10.~~ **Done**, along with 12, 13 and 14.
- ~~**Stage B — a Mac guest.** Seams 3, 4, 5 and 6.~~ **Done.**
- **Stage D — distribution.** `scripts/publish-app.sh` builds a self-contained `Josour.app` and `.dmg` (neither needs
  .NET installed). **Signing** remains: the bundle is unsigned, so the first run on another machine needs a
  "right-click → Open" once, which [ADR-0011](decisions/0011-no-code-signing-certificate.md) leaves open on macOS
  specifically, because Gatekeeper is stricter than SmartScreen.
- **Field verification.** A mac ↔ Windows session in both roles, on two machines.

## How it is built on a Mac today

The SDK is in `~/.dotnet` and not on `PATH`:

```bash
export PATH="$HOME/.dotnet:$PATH"
cd client && dotnet build Josour.sln && dotnet test Josour.sln
```

`Josour.App` is now Avalonia targeting `net8.0` alongside `net8.0-windows10.0.19041.0`, and it builds **and runs** on a
Mac. The Windows target is kept for the toast notifications alone, and it builds on a Mac thanks to
`EnableWindowsTargeting`.
