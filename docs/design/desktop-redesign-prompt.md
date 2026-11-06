# Prompt: redesign the Josour desktop UI (Windows and macOS) around the qantara identity

> Paste everything below the line into a new Claude Code session opened at the repo root (`josour/`),
> or tell the session: "Read docs/design/desktop-redesign-prompt.md and follow it."

---

Redesign the UI and UX of the Josour desktop app on Windows and macOS around the new identity in `1-qantara/`.
Work in four phases, and stop after Phase 1 and after Phase 2 so I can review and decide before you continue.
Talk to me in Arabic in chat; write design pages, code, comments and commit messages in English.

## Context: what exists today

**The desktop app** is `client/src/Josour.App`: Avalonia 11.3 with FluentTheme. The same views build for Windows
(`net8.0-windows10.0.19041.0`) and macOS (`net8.0`); ADR-0013 (`docs/decisions/0013-avalonia-and-macos.md`) explains why.
- Windows and pages are in `Views/`. `MainWindow` is 480×640, with two tabs: contacts (`ContactsPage`) and sharing
  (`HostPage`). During a session, `SessionPanel` takes over the whole window. A status strip runs along the bottom.
  The other windows are `LoginWindow`, `FirstRunWindow`, `SettingsWindow`, `AboutWindow`, `AdminWindow`,
  `IncomingRequestWindow`, `LicensesWindow` and `FatalErrorWindow`.
- Controls are in `Controls/`: `Card`, `Icon`/`Icons`, `InfoBar` and `Typography.axaml`. The type scale is only a set
  of classes. Secondary text is drawn with opacity. There are no tokens.
- `App.axaml` pins `RequestedThemeVariant="Light"`, so there is no dark mode.
- The only icon is `Assets/josour.ico`, which is the old identity. It feeds the tray icon and the exe icon. It also
  feeds the `.icns` built in `scripts/publish-app.sh` and the Windows installer `client/installer/Josour.iss`.
- Reading direction is fixed once in `UiFlow.cs`. Technical fields (URL, email, IP, version) always stay LTR.
- Strings are shared with the phone in `client/src/Josour.Infrastructure/Localization/` (`StringsAr.resx`,
  `StringsEn.resx`, `UiStringKeys.cs`). The desktop reads them through `Strings.cs`.
- Notifications are Windows toasts with Accept/Reject buttons (`WindowsToastNotifier`) and `MacNotifier` on macOS.
  The tray icon and its menu are in `Services/TrayService.cs`; on macOS this is the menu bar item.
- Tests are in `client/tests/Josour.App.Tests`. They run headless (`Rendered.cs`, `ViewsLoadTests.cs`,
  `ControlsTests.cs`, …).

**The phone app was just redesigned** (v2.6.0, commit `68516f4`). It is the reference the desktop must be consistent with:
- `docs/design/mobile-phase1-foundation.html`: the foundation and the approved decisions.
- `docs/design/mobile-phase2-mockups.html`: the mockups and the copy table.
- `docs/design/mobile-tokens.json`: the approved tokens. These cover the teal/ink palettes, light and dark,
  semantic colours, font, spacing and radii.
- `scripts/mobile-theme.py` (with `--check`) generates the phone's theme from the tokens and the logo files. Its outputs
  are `Josour.Mobile/Theme/Tokens.axaml`, `Controls/BrandGeometry.g.cs` and the Android resources. Logo path data is
  copied as it is, never redrawn.
- Brand components are in `client/src/Josour.Mobile/Controls`: `BrandLockup`, `Motif` (the arch), `Icon` and `Banner`.
  The theme is in `Theme/` (`ControlThemes.axaml`, `Styles.axaml`).

**Approved identity decisions. They bind the desktop too:**
- **Font:** Readex Pro, the wordmark's own typeface, for both Arabic and Latin. Use the four static weights (OFL) in
  `Josour.Mobile/Assets/Fonts`. Do not use Noto Naskh, and do not use Segoe or SF as the primary UI font.
- **Primary colour:** teal `#0E7490` in light mode and `#5ECCE4` in dark mode.
- **Logo on dark:** the logo is white on dark backgrounds.
- **Logo files:**
  - Symbols: `symbol-teal/black/white.svg`.
  - Lock-ups: `lockup-ar-first-rtl.svg` for Arabic and `lockup-en-first-ltr.svg` for English.
  - Small-size proofs at 16 and 24 px.
- **The arch:** the arch motif is the visual language for status (active, idle, disconnected). A hero may break the arch.
- **Semantic colours:** success, warning, danger and info come from the tokens. No hex values in any view.

## Hard constraints (read these first)

1. **Don't run the real app on this Mac.** This Mac has a real Josour install signed in to my production server
   (`~/Library/Application Support/Josour`).
   - Never launch the real `Josour.App`, never run `--proxy-guardian` or `--restore-proxy`, and never run anything that
     reaches `AppPaths.LocalAppDataRoot`.
   - Setting `HOME` does not isolate it.
   - For visual checks, render headlessly with Skia, either in a scratchpad harness or in the test project.
   - `App.Start()` runs only under a desktop lifetime. Don't break that.
2. **Tooling:** the .NET SDK is at `~/.dotnet/dotnet` and is not on PATH. macOS has no `timeout` command.
3. **Don't break the phone.** Editing a shared string key also changes the phone. Tag every change as one of:
   - `new Desktop*`: a desktop-only key.
   - `edit-shared`: the same meaning in better words, for both platforms.
   - `same`: unchanged.

   `scripts/mobile-theme.py --check` and `Josour.Mobile.Tests` must stay green.
4. **One source of truth for the identity.** Don't copy colours by hand into the desktop. In Phase 1, propose one of
   these, with cost and risk, and recommend one:
   - Extend the generator so it also emits a desktop `Tokens.axaml` from the same file. Rename the file to
     `brand-tokens.json` if needed.
   - Create a shared project, such as `Josour.Brand`, that holds the tokens, the font and the geometry for both apps.
5. **No behaviour or protocol changes.** View-model additions are allowed only to serve the UI, for example a hero
   status or a confirmation before a risky action, like `HostStatus` on the phone. Bring anything bigger to me first.
6. **Git:** no commit, push or release unless I ask explicitly. Work on a branch, not on `main`.

## What I want from the design

**One identity, and each platform's own conventions.** The app must clearly read as Josour on both systems, and behave the way each system's users expect.

- **Windows 10/11:**
  - Put the identity on a standard window frame. Evaluate Mica/Acrylic on Windows 11, with a solid fallback on
    Windows 10.
  - The tray icon must stay legible at 16 and 24 px, on both light and dark taskbars.
  - Generate a multi-size `.ico` (16, 20, 24, 32, 48, 64, 256) from `symbol-*.svg`.
  - Cover the toast icon and app logo, and the installer art (`Josour.iss` and any wizard images).
  - Use Ctrl shortcuts and Windows button order in dialogs.
- **macOS:**
  - The menu bar icon is a monochrome **template image** (black plus alpha) that the system tints. It is not a
    coloured `.ico`.
  - Generate an `.icns` in the macOS shape (a squircle with the system margin) from the logo. This replaces converting
    the current `josour.ico` in `publish-app.sh`.
  - Respect the traffic lights and the title area.
  - Add Cmd shortcuts: `Cmd+,` for settings, `Cmd+W` and `Cmd+Q`.
  - Add an app menu (About, Settings, Quit) through `NativeMenu`.
  - Use macOS button order in dialogs.
- **Light and dark** follow the system setting while the app runs, as on the phone. Remove the `Light` pin.
- **Direction:**
  - Arabic is fully RTL. Mirror only directional glyphs, through `UiFlow.GlyphMirror`.
  - Use the Arabic-first lock-up in Arabic and the English-first lock-up in English.
- **Accessibility:**
  - Contrast of at least AA (the tokens already compute it).
  - A visible focus ring in the `focus` colour.
  - Full keyboard navigation.
  - `AutomationProperties.Name` on every icon button.
  - Status never depends on colour alone.
  - Correct at 100%, 150% and 200% DPI on Windows, and on Retina on macOS.

**UX: review every screen, not just its look.**
- **`MainWindow`:**
  - Does it stay a narrow 480×640 window with two tabs? Or should it become a layout that uses the desktop, such as a
    sidebar or a status hero on top, while staying small like a tray app?
  - Propose at least two alternatives and recommend one.
  - Is the bottom status strip (settings, about, admin, version) in the right place?
- **`HostPage`:**
  - Being available to share is the heart of the app. Give it a hero with the arch motif, as on the phone.
  - There are four warnings: not connected, firewall, VPN and simulated server. Rank them by priority instead of
    stacking them.
- **`ContactsPage`, `SessionPanel` and `IncomingRequestWindow`:**
  - An incoming access request is a sensitive moment. The decision (accept, reject, trust) must be clear and safe.
  - Ask for confirmation before turning on trust.
  - Show an "expired" state instead of making the request vanish, as on the phone.
- **`LoginWindow` and `FirstRunWindow`:** these make the first impression, so show the lock-up. Show the sign-out reason
  when there is one.
- **`SettingsWindow`, `AboutWindow`, `AdminWindow`, `LicensesWindow` and `FatalErrorWindow`:** move them all onto the new
  components.
- **Tray menu and notifications on both systems:** review the wording, the icons and the icon state. Should the tray icon
  change with the status (available, session active, not connected)?

## Phases

### Phase 1: foundation and audit (stop afterwards)
- **Inventory:** every screen, component, string and icon in `Josour.App`. Include a headless screenshot of each
  window, in Arabic and English, as it looks today.
- **UX audit:** problems ranked by impact, plus anything that goes against either platform's conventions.
- **Desktop tokens:** what to take as is from the phone, and what only the desktop needs. That includes higher density,
  window sizes, hover, the mouse cursor, focus states, and elevation for windows and menus.
- **Source of truth:** the decision from constraint 4.
- **Icon plan for each platform:** include real rendered proofs at 16, 20, 24 and 32 px, and the macOS template image
  on light and dark menu bars.
- **Numbered decisions:** a list I can answer one by one with yes, no or an alternative.
- **Output:**
  - The page `docs/design/desktop-phase1-foundation.html`, in the same style as the phone pages. Publish it as an
    artifact and give me the link.
  - `docs/design/desktop-tokens.json`, or the unified tokens file.

### Phase 2: mockups (stop afterwards)
- **Mockups:** every screen in light and dark, Arabic and English. Show the main windows in both a Windows frame and a
  macOS frame, at least `MainWindow`, `IncomingRequestWindow` and `LoginWindow`.
- **States for each screen:** empty, loading, error, not connected, session active and request expired.
- **Copy table:** the key, the Arabic and English text before and after, and the tag (`new Desktop*`, `edit-shared` or
  `same`). Highlight every `edit-shared`, because it changes the phone.
- **View-model additions:** propose each one with its reason.
- **Output:** `docs/design/desktop-phase2-mockups.html`, published as an artifact.

### Phase 3: implementation (after my approval)
- **Order:** tokens and generator first. Then the font and the brand components (lock-up, motif, icons), shared or
  generated. Then ControlThemes on top of Fluent. Then the screens one at a time.
- **Icons:** generate them with a script from `1-qantara/`. That covers `.ico`, `.icns`, the macOS tray template, and
  the toast and installer icons. The script needs a `--check` mode, and `publish-app.sh` and `publish-exe.ps1` must
  call it or verify it.
- **No scattered values:** no hex values or FontSize in views. Everything goes through tokens and classes.
- **Licences:** update `NOTICE` and `OFL.txt` if the font ships in the desktop package.

### Phase 4: verification
- **Rendering tests:** headless tests that draw every window in both languages and both themes, and at 200% scale.
  Check that Arabic is never clipped or overlapping.
- **Behaviour tests:** tests for any new view-model behaviour.
- **Green builds:** `dotnet test` for the whole `client/Josour.sln` is green, and the generators' `--check` is green.
- **Verification page:** `docs/design/desktop-phase4-verification.html`, with before and after screenshots.
  **Do not show my machine's hostname or my server's address in it.**
- **Unverified list:** an explicit list of what was not verified on real hardware. That includes real Windows, Mica,
  toasts, Narrator and VoiceOver, and the menu bar icon on a real Mac. I will check those myself.
- **Drafts:** a draft `CHANGELOG.md` entry and commit message, **without** committing.

## How to work
- Before proposing anything, read the phone's design pages, the tokens and the generator in full, and follow their
  approved decisions. If one of them does not suit the desktop, raise it as a numbered decision. Don't change it silently.
- At each stop, give me the page link, the list of decisions and anything still open.
- If you find something out of scope (a bug, a server improvement, the web admin dashboard), record it as a follow-up.
  Don't do it.
