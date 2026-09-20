# The installer

- `Josour.iss`: the Inno Setup script. It adds an inbound Windows Firewall rule for the executable (the tunnel's
  temporary listener) and removes it on uninstall. It requires administrator rights.
- Release steps (on Windows):
  1. `dotnet publish src/Josour.App -c Release -r win-x64 --self-contained false -o publish/app`
  2. `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a publish\app\Josour.exe`
  3. `iscc /DAppVersion=<x.y.z> Josour.iss`
  4. `signtool sign ... Output\Josour-Setup-<x.y.z>.exe`
- The signing certificate (OV/EV) is requested on day one; without it SmartScreen warns at install time.
- Toast notifications register a CLSID under `HKCU\Software\Classes` on first use; on uninstall the installer must
  call `Josour.exe --uninstall-notifications` (a switch added in week 2 that calls
  `ToastNotificationManagerCompat.Uninstall()`).
- The name `Josour Tunnel` for the firewall rule is what `FirewallRuleChecker` in the application checks; do not
  change it in one place without the other.
