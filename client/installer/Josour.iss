; Inno Setup script for Josour (ADR-0005: signed unpackaged installer)
; Build: iscc /DAppVersion=0.1.0 /DPublishDir=..\publish\app Josour.iss
; Sign: signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a Output\Josour-Setup-0.1.0.exe

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\app"
#endif

[Setup]
AppId={{7E1C2D5A-4B2F-4C7E-9B1A-8F3C2A9D5E41}
AppName=Josour
AppVersion={#AppVersion}
AppPublisher=Josour
DefaultDirName={autopf}\Josour
DefaultGroupName=Josour
OutputDir=Output
OutputBaseFilename=Josour-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
WizardStyle=modern
UninstallDisplayIcon={app}\Josour.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Josour"; Filename: "{app}\Josour.exe"
Name: "{autodesktop}\Josour"; Filename: "{app}\Josour.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Start Josour when I sign in to Windows"; GroupDescription: "Startup"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Josour"; ValueData: """{app}\Josour.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; An inbound firewall rule for the executable (the tunnel's temporary listener). It is removed on uninstall.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Josour Tunnel"""; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Josour Tunnel"" dir=in action=allow program=""{app}\Josour.exe"" enable=yes profile=domain,private,public protocol=TCP"; Flags: runhidden
Filename: "{app}\Josour.exe"; Description: "Launch Josour"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Unregistering the toast notifications (the CLSID under HKCU\Software\Classes) before deleting the files
Filename: "{app}\Josour.exe"; Parameters: "--uninstall-notifications"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallNotifications"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Josour Tunnel"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
