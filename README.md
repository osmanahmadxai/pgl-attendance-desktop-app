# PGL Attendance — Desktop App

Native Windows desktop application that receives attendance device data on a
configurable TCP port (`/iclock/cdata`) and forwards it to the HRMIS API.

- **Background service** (`PglAttendanceService.exe`) — receives device data 24/7,
  runs as a Windows Service, auto-starts at boot, auto-restarts on crash.
- **Desktop UI** (`PglAttendance.exe`) — native WinForms window with stats,
  records grid, sync controls, settings. Closing it does **not** stop the service.

No Node.js, no .NET runtime install required on the target PC — both exes are
self-contained .NET 8 single-file binaries.

## Install on any Windows PC (one PowerShell line)

```powershell
irm https://github.com/ozmanghani/pgl-attendance-desktop-app/releases/latest/download/PGL-Attendance-Setup.exe -OutFile $env:TEMP\pgl.exe; & $env:TEMP\pgl.exe
```

Run as **Administrator**. The installer is fully self-contained — devices can then
POST to `http://<PC_IP>:4001/iclock/cdata` (port is configurable in Settings).

## Build the installer yourself

See [windows-build/README.md](windows-build/README.md). Build prerequisites
(Windows machine): .NET 8 SDK + Inno Setup 6.

```powershell
pwsh -ExecutionPolicy Bypass -File .\windows-build\scripts\build.ps1
```

CI also builds an installer on every `v*` tag push — see
`.github/workflows/build-installer.yml`.

## Project layout

```
.
├── windows-build/
│   ├── native/                  # C# .NET 8 solution (3 projects)
│   │   ├── PglAttendance.Core/      # Shared lib (models, repo, sync)
│   │   ├── PglAttendance.Service/   # ASP.NET Core Kestrel + sync engine
│   │   └── PglAttendance.Desktop/   # WinForms native UI
│   ├── installer/installer.iss      # Inno Setup script
│   ├── scripts/build.ps1            # Build pipeline
│   └── assets/app.ico
├── logo.png                     # Source for app.ico
└── .github/workflows/           # CI to build + release the installer
```
