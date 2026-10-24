; Installer for Mero Lekhapal (the accounting desktop program). Build with build-installer.ps1 (it publishes the
; program into out\app first), or: ISCC.exe MeroLekhapal.iss

#define AppName "Mero Lekhapal"
#define AppExe "MeroLekhapal.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define FirewallRule "Mero Lekhapal (phone app)"
#define FirewallBlock "Mero Lekhapal (public networks)"

[Setup]
AppId={{5B8E2F1A-6C3D-4E7B-9A21-ACC0A11CA7E1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\Mero Lekhapal
DefaultGroupName=Mero Lekhapal
DisableProgramGroupPage=yes
; Admin rights: the program goes to Program Files and the firewall rule for the phone app is added.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=out
OutputBaseFilename=MeroLekhapalSetup
SetupIconFile=..\src\Accounting.Desktop\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} - Accounting & Inventory
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=logo\wizard.png
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "phone"; Description: "Let the phone app connect over Wi-Fi (home and office networks only)"; GroupDescription: "Phone app:"

[Files]
Source: "out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\Mero Lekhapal"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Mero Lekhapal"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The screens need the Microsoft Edge WebView2 Runtime (already part of Windows 11).
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Check: NeedsWebView2; Flags: waituntilterminated
; Remove rules from an earlier install, then allow the phone on private networks and quietly block
; public ones (so Windows does not pop up its own firewall question on a cafe Wi-Fi).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallBlock}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP profile=private,domain enable=yes"; Flags: runhidden waituntilterminated; Tasks: phone
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallBlock}"" dir=in action=block program=""{app}\{#AppExe}"" profile=public enable=yes"; Flags: runhidden waituntilterminated; Tasks: phone
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallBlock}"" dir=in action=block program=""{app}\{#AppExe}"" enable=yes"; Flags: runhidden waituntilterminated; Tasks: not phone
Filename: "{app}\{#AppExe}"; Description: "Open Mero Lekhapal now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DelFirewallAllow"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallBlock}"""; Flags: runhidden; RunOnceId: "DelFirewallBlock"

[Messages]
FinishedLabel=Mero Lekhapal is installed. Your company data is kept in your Windows user folder (%LOCALAPPDATA%\Mero Lekhapal), so it stays safe when you update or uninstall the program.

[Code]
const
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2(Root: Integer; Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function NeedsWebView2: Boolean;
begin
  Result := not (HasWebView2(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
    or HasWebView2(HKLM, WebView2Key) or HasWebView2(HKCU, WebView2Key));
end;
