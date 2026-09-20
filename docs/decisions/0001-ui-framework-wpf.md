# ADR-0001: the client interface on WPF and .NET 8

**Status:** **superseded by [ADR-0013](0013-avalonia-and-macos.md)** on 2026-09-18 (it was never accepted). Its
reasoning still holds on Windows, but supporting macOS took the choice out of WPF's hands.

## Context
The product document left the choice between WinUI 3 and WPF to a short technical prototype. The application lives
in the system tray most of the time and needs notifications with accept/reject buttons from outside MSIX.

## Decision
WPF on .NET 8 with `WPF-UI` for the look, `CommunityToolkit.Mvvm`, `H.NotifyIcon.Wpf` and
`Microsoft.Toolkit.Uwp.Notifications`.

## Reasons
- WinUI 3 has no official tray support, needs the Windows App SDK Runtime, and its notification story outside MSIX
  is newer and less settled.
- Every WPF component this needs is mature and stable, which is less risk on a nine-week schedule.

## Consequences
- A modern enough look through WPF-UI; not native Fluent.
- Moving to WinUI 3 later stays possible, because all the logic lives in libraries that know nothing about the
  interface.
