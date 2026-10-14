; FocusMed Installer — Inno Setup script.
; Packages the single-folder deployment (deploy/) into C:\Program Files\FocusMed.
; Post-install it starts the Launcher with --autostart, which runs the whole
; bootstrap: folders, firewall rule, ONLOGON autostart task, virtual printer, DB migrate.
;
; VERSIONING: bump MyAppVersion per release; NEVER change AppId (same AppId = in-place
; upgrade, operator config.json + database preserved). No OutputDir directive: the
; default (script directory) keeps builds portable — no machine-absolute paths.
; SourceDir stays relative (..\deploy) for the same reason.

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
; Main payload. config.json is EXCLUDED here and installed by the entry below with
; onlyifdoesntexist: upgrades must never overwrite the operator's tuned config, and
; fresh installs without a packaged config.json force first-boot autodetect.
; (No dev config.json is shipped: CI publishes into an empty deploy/ so none exists;
; for local builds delete deploy\config.json before compiling the installer.)
Source: "*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "BuildHost-*,*.pdb,package.json,package-lock.json,appsettings.json,appsettings.Development.json,config.json,web.config,runtimes\linux*,runtimes\osx*,runtimes\maccatalyst*,runtimes\win-arm64*,runtimes\win-x86*,FocusMedSetup-*.exe"
Source: "config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist

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

{ Pre-upgrade DB backup: before any file is replaced, copy the live database into
  %LOCALAPPDATA%\FocusMed\backups\ so a failed upgrade never loses the clinic's data. }
procedure BackupLiveDatabase;
var
  DataDir, DbPath, BackupDir, BackupPath: String;
begin
  try
    if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'FOCUSMED_DATA', DataDir) then
      DataDir := '';
    if DataDir = '' then
      DataDir := ExpandConstant('{localappdata}\FocusMed');
    DbPath := DataDir + '\focusmed.db';
    if not FileExists(DbPath) then
      exit;
    BackupDir := DataDir + '\backups';
    if not DirExists(BackupDir) then
      CreateDir(BackupDir);
    BackupPath := BackupDir + '\focusmed-preupgrade-' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '.db';
    if CopyFile(DbPath, BackupPath, False) then
      Log('Pre-upgrade DB backup: ' + BackupPath)
    else
      Log('Pre-upgrade DB backup FAILED for ' + DbPath);
  except
    Log('Pre-upgrade DB backup raised (continuing install)');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    BackupLiveDatabase;
end;

function InitializeSetup(): Boolean;
begin
  KillFocusMed;
  Result := True;
end;