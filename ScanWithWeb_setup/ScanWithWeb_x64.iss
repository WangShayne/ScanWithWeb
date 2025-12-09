; ScanWithWeb Service Installer (64-bit)
; Inno Setup Script
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "ScanWithWeb Service"
#define MyAppVersion "2.0.8"
#define MyAppPublisher "ScanWithWeb Team"
#define MyAppURL "https://github.com/user/scanwithweb"
#define MyAppExeName "ScanWithWeb.exe"

[Setup]
; Application info
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Install directories
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\dist\installer
OutputBaseFilename=ScanWithWeb_Setup_x64_v{#MyAppVersion}
; SetupIconFile requires ICO format - using default if not available
Compression=lzma2/ultra64
SolidCompression=yes

; Privileges
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit only
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; UI settings
WizardStyle=modern
WizardSizePercent=100

; Uninstall info
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (64-bit)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "Startup options:"

[Files]
; Main application files
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; TWAIN Data Source Manager (required for scanner functionality)
; Install to System32 only if not already present (onlyifdoesntexist)
; uninsneveruninstall - don't remove on uninstall as other apps may need it
Source: "dependencies\twaindsm_x64.dll"; DestDir: "{sys}"; DestName: "twaindsm.dll"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall

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
    'This will install {#MyAppName} {#MyAppVersion} (64-bit) on your computer.' + #13#10 + #13#10 +
    'ScanWithWeb is a local service that enables web applications to access TWAIN scanners.' + #13#10 + #13#10 +
    'Features:' + #13#10 +
    '  - WebSocket server for scanner communication' + #13#10 +
    '  - Secure WSS (HTTPS) support' + #13#10 +
    '  - System tray integration' + #13#10 + #13#10 +
    'Click Next to continue.';
end;
