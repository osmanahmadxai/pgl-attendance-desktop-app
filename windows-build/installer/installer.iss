; PGL Attendance — native installer (no Node, no Next.js, no NSSM).
; Installs:
;   • PglAttendanceService.exe   (Windows Service, started via sc.exe)
;   • PglAttendance.exe          (native WinForms desktop UI)
; Both are .NET 8 self-contained single-file exes (no .NET install needed on target).

#define MyAppName         "PGL Attendance"
#define MyAppPublisher    "PGL"
#define MyAppExeName      "PglAttendance.exe"
#define MyServiceName     "PGLAttendanceSync"
#define MyServiceDisplay  "PGL Attendance Sync"
#define MyServiceExe      "PglAttendanceService.exe"
#ifndef MyAppVersion
  #define MyAppVersion    "1.0.0"
#endif

#ifndef StagingDir
  #define StagingDir "..\dist\staging"
#endif

[Setup]
AppId={{2C7B6F32-9F1A-4F8B-9C53-PGLATT-0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL=https://pglsystem.com
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputBaseFilename=PGL-Attendance-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#StagingDir}\app.ico
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "autostartapp"; Description: "Launch {#MyAppName} at sign-in"; GroupDescription: "Startup:"

[Dirs]
Name: "{commonappdata}\{#MyAppName}";       Permissions: users-modify
Name: "{commonappdata}\{#MyAppName}\logs";  Permissions: users-modify

[Files]
Source: "{#StagingDir}\{#MyServiceExe}"; DestDir: "{app}";       Flags: ignoreversion
Source: "{#StagingDir}\{#MyAppExeName}"; DestDir: "{app}";        Flags: ignoreversion
Source: "{#StagingDir}\app.ico";          DestDir: "{app}";        Flags: ignoreversion onlyifdoesntexist
Source: "{#StagingDir}\seed\settings.json";  DestDir: "{commonappdata}\{#MyAppName}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#StagingDir}\seed\attendance.db";  DestDir: "{commonappdata}\{#MyAppName}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\Uninstall {#MyAppName}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
; --- Deregister the previous service (upgrades) ---
; It was already stopped in PrepareToInstall (see [Code]); this only removes the
; registration so it can be recreated against the current {app} path. The stop
; below is a harmless no-op kept for the case where the service was re-started
; between the two steps.
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#MyServiceName}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden waituntilterminated

; --- Register the new service ---
Filename: "{sys}\sc.exe"; Parameters: "create ""{#MyServiceName}"" binPath= ""\""{app}\{#MyServiceExe}\"""" DisplayName= ""{#MyServiceDisplay}"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description ""{#MyServiceName}"" ""Receives attendance device data on the configured port and forwards it to HRMIS. Part of {#MyAppName}."""; Flags: runhidden waituntilterminated
; Restart on failure: 1st = 3s, 2nd = 3s, subsequent = 5s; counter resets after 60s
Filename: "{sys}\sc.exe"; Parameters: "failure ""{#MyServiceName}"" reset= 60 actions= restart/3000/restart/3000/restart/5000"; Flags: runhidden waituntilterminated

; --- Firewall: open the default device port so a fresh install works at once.
; From here on the service reconciles these rules itself on every start and
; whenever the ports change, so changing the port in Settings no longer leaves
; the firewall pointing at the old one. Rule names must match
; PglAttendance.Service\Security\FirewallManager.cs.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} (device)"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""{#MyAppName} (device)"" dir=in action=allow protocol=TCP localport=4001"; Flags: runhidden waituntilterminated

; --- Start the service ---
Filename: "{sys}\sc.exe"; Parameters: "start ""{#MyServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting {#MyAppName} service..."

; --- Launch desktop app at the end of install ---
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#MyServiceName}""";   Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelSvc"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFw"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} (device)"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwDevice"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} (admin)"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwAdmin"
Filename: "taskkill"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "KillUi"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PGLAttendance"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostartapp; Flags: uninsdeletevalue

[UninstallDelete]
; Intentionally leave %PROGRAMDATA%\PGL Attendance\ in place so the DB + settings survive uninstall.

[Code]
// ---------------------------------------------------------------------------
// Free the old service binary BEFORE [Files] copies the new one.
//
// Inno installs [Files] first and only runs [Run] — where sc.exe stops the
// service — afterwards. On an upgrade that ordering is fatal: the old service
// is still running, Windows holds its own exe locked, and the copy cannot
// replace it. sc delete/create/start then re-registers and cheerfully starts
// the OLD binary, while Setup reports success. Rebooting does not help either,
// because without the 'restartreplace' flag nothing was ever queued for
// replacement at boot.
//
// PrepareToInstall runs before any file is copied, which is the only place this
// can be fixed. Deleting the old exe — rather than merely asking the service to
// stop — is what proves the lock is actually gone.
// ---------------------------------------------------------------------------
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, I: Integer;
  ExePath: String;
begin
  Result := '';
  NeedsRestart := False;

  // Both of these hold files this installer replaces.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop "{#MyServiceName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im "{#MyAppExeName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  ExePath := ExpandConstant('{app}\{#MyServiceExe}');
  if not FileExists(ExePath) then
    Exit;   // fresh install: nothing to free

  // The SCM stops services asynchronously, so poll instead of assuming. If the
  // service is wedged and ignores the stop, kill it outright — it is about to
  // be deleted and recreated anyway.
  for I := 1 to 30 do
  begin
    if DeleteFile(ExePath) or (not FileExists(ExePath)) then
      Exit;
    if I = 5 then
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im "{#MyServiceExe}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  // Never silently install over a locked binary: failing loudly here is the
  // whole point, because the alternative looks like a successful upgrade.
  Result := 'Setup could not stop the {#MyServiceDisplay} service, so the previous version could not be replaced.' + #13#10 + #13#10 +
            'Close {#MyAppName}, then run this installer again.';
end;

function NeedRestart(): Boolean;
begin
  Result := False;
end;
