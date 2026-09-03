; Inno Setup script for RouteBridge (ADR-0005: signed unpackaged installer)
; Build: iscc /DAppVersion=0.1.0 /DPublishDir=..\publish\app RouteBridge.iss
; Sign: signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a Output\RouteBridge-Setup-0.1.0.exe

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\app"
#endif

[Setup]
AppId={{7E1C2D5A-4B2F-4C7E-9B1A-RouteBridge01}
AppName=RouteBridge
AppVersion={#AppVersion}
AppPublisher=RouteBridge
DefaultDirName={autopf}\RouteBridge
DefaultGroupName=RouteBridge
OutputDir=Output
OutputBaseFilename=RouteBridge-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
WizardStyle=modern
UninstallDisplayIcon={app}\RouteBridge.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RouteBridge"; Filename: "{app}\RouteBridge.exe"
Name: "{autodesktop}\RouteBridge"; Filename: "{app}\RouteBridge.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Start RouteBridge when I sign in to Windows"; GroupDescription: "Startup"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RouteBridge"; ValueData: """{app}\RouteBridge.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; قاعدة جدار ناري واردة للتنفيذي (المستمع المؤقت للنفق). تُحذف عند الإزالة.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""RouteBridge Tunnel"""; Flags: runhidden; StatusMsg: "Configuring Windows Firewall..."
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""RouteBridge Tunnel"" dir=in action=allow program=""{app}\RouteBridge.exe"" enable=yes profile=domain,private,public protocol=TCP"; Flags: runhidden
Filename: "{app}\RouteBridge.exe"; Description: "Launch RouteBridge"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""RouteBridge Tunnel"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
