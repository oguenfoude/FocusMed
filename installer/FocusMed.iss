; FocusMed Installer — Inno Setup script.
; Packages the single-folder deployment (deploy/) into C:\Program Files\FocusMed.
; Post-install it starts the Launcher with --autostart, which runs the whole
; bootstrap: folders, firewall rule, ONLOGON autostart task, virtual printer, DB migrate.

#define MyAppName "FocusMed"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "FocusMed"
#define MyAppExeName "FocusMed.Launcher.exe"

[Setup]
AppId={{A4C9F1E0-8B2F-4D7E-9C1A-3F6E2B8A4D5C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\FocusMed
DefaultGroupName=FocusMed
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
CloseApplications=force
OutputDir=D:\FocusMed\installer
OutputBaseFilename=FocusMedSetup-{#MyAppVersion}
SetupIconFile=..\src\FocusMed.Launcher\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}
SourceDir=..\deploy

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "BuildHost-*;*.pdb;package.json;package-lock.json;appsettings.Development.json;web.config"

[Icons]
Name: "{group}\FocusMed"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\FocusMed"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait skipifsilent; StatusMsg: "Starting FocusMed (Worker + Dashboard)..."

[UninstallRun]
Filename: "taskkill"; Parameters: "/IM FocusMed.Worker.exe /F /IM FocusMed.Dashboard.exe /F /IM FocusMed.Launcher.exe /F"; Flags: runhidden; RunOnceId: "killprocesses"
Filename: "schtasks"; Parameters: "/Delete /TN ""FocusMed"" /F"; Flags: runhidden; RunOnceId: "deleteautostart"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""FocusMed DICOM TCP 11112"""; Flags: runhidden; RunOnceId: "deletefirewall"

[Code]
procedure KillFocusMed;
var
  PID: Integer;
begin
  Exec('taskkill.exe', '/IM FocusMed.Worker.exe /F /T', '', 0, ewWaitUntilTerminated, PID);
  Exec('taskkill.exe', '/IM FocusMed.Dashboard.exe /F /T', '', 0, ewWaitUntilTerminated, PID);
  Exec('taskkill.exe', '/IM FocusMed.Launcher.exe /F /T', '', 0, ewWaitUntilTerminated, PID);
end;

function InitializeSetup(): Boolean;
begin
  KillFocusMed;
  Result := True;
end;