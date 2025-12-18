; ScanWithWeb Service Installer (32-bit)
; Inno Setup Script
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "ScanWithWeb Service"
#define MyAppVersion "3.0.9"
#define MyAppPublisher "ScanWithWeb Team"
#define MyAppURL "https://github.com/user/scanwithweb"
#define MyAppExeName "ScanWithWeb.exe"

[Setup]
; Application info
AppId={{B2C3D4E5-F6A7-8901-BCDE-F12345678901}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Install directories
DefaultDirName={autopf32}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\dist\installer
OutputBaseFilename=ScanWithWeb_Setup_x86_v{#MyAppVersion}
; SetupIconFile requires ICO format - using default if not available
Compression=lzma2/ultra64
SolidCompression=yes

; Privileges
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 32-bit compatible
ArchitecturesAllowed=x86compatible

; UI settings
WizardStyle=modern
WizardSizePercent=100

; Uninstall info
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (32-bit)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "Startup options:"

[Files]
; Main application files
Source: "..\dist\win-x86\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; TWAIN Data Source Manager (required for scanner functionality)
; For 32-bit apps: Install to SysWOW64 on 64-bit Windows, System32 on 32-bit Windows
; onlyifdoesntexist - don't overwrite existing TWAIN DSM
; uninsneveruninstall - don't remove on uninstall as other apps may need it
Source: "dependencies\twaindsm_x86.dll"; DestDir: "{syswow64}"; DestName: "twaindsm.dll"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall; Check: IsWin64
Source: "dependencies\twaindsm_x86.dll"; DestDir: "{sys}"; DestName: "twaindsm.dll"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall; Check: not IsWin64

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the application before uninstall
Filename: "taskkill"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
// Check if application is running during uninstall
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Try to close the application gracefully
  Exec('taskkill', '/IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

// Custom messages
procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption :=
    'This will install {#MyAppName} {#MyAppVersion} (32-bit) on your computer.' + #13#10 + #13#10 +
    'ScanWithWeb is a local service that enables web applications to access TWAIN scanners.' + #13#10 + #13#10 +
    'Features:' + #13#10 +
    '  - WebSocket server for scanner communication' + #13#10 +
    '  - Secure WSS (HTTPS) support' + #13#10 +
    '  - System tray integration' + #13#10 + #13#10 +
    'Click Next to continue.';
end;
