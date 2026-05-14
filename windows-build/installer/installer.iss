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
; --- Stop any previously installed service (upgrades) ---
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#MyServiceName}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden waituntilterminated

; --- Register the new service ---
Filename: "{sys}\sc.exe"; Parameters: "create ""{#MyServiceName}"" binPath= ""\""{app}\{#MyServiceExe}\"""" DisplayName= ""{#MyServiceDisplay}"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description ""{#MyServiceName}"" ""Receives attendance device data on the configured port and forwards it to HRMIS. Part of {#MyAppName}."""; Flags: runhidden waituntilterminated
; Restart on failure: 1st = 3s, 2nd = 3s, subsequent = 5s; counter resets after 60s
Filename: "{sys}\sc.exe"; Parameters: "failure ""{#MyServiceName}"" reset= 60 actions= restart/3000/restart/3000/restart/5000"; Flags: runhidden waituntilterminated

; --- Firewall: allow inbound on the listening port (default 4001) ---
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""{#MyAppName}"" dir=in action=allow protocol=TCP localport=4001"; Flags: runhidden waituntilterminated

; --- Start the service ---
Filename: "{sys}\sc.exe"; Parameters: "start ""{#MyServiceName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Starting {#MyAppName} service..."

; --- Launch desktop app at the end of install ---
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#MyServiceName}""";   Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#MyServiceName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelSvc"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFw"
Filename: "taskkill"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "KillUi"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PGLAttendance"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostartapp; Flags: uninsdeletevalue

[UninstallDelete]
; Intentionally leave %PROGRAMDATA%\PGL Attendance\ in place so the DB + settings survive uninstall.

[Code]
function NeedRestart(): Boolean;
begin
  Result := False;
end;
