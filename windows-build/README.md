# PGL Attendance — Windows packaging (native C# build)

Builds a single `PGL-Attendance-Setup-x.y.z.exe`. Run it once on a Windows PC,
get a native desktop app + a background Windows Service that receives device
data on the configured port. No Node, no Next.js, no Electron, no NSSM.

## Runtime architecture

```
┌─ Windows host ────────────────────────────────────────────────────────┐
│                                                                       │
│   ┌─────────────────────────────────────────────────────────┐         │
│   │  PGLAttendanceSync           (Windows Service)          │         │
│   │  • PglAttendanceService.exe (self-contained .NET 8)     │         │
│   │  • Kestrel HTTP server on the configured port           │         │
│   │      └─ POST /iclock/cdata     ◄── attendance devices   │         │
│   │      └─ GET  /attendance, /stats, /unsynced-ids …       │         │
│   │      └─ GET/PUT /api/settings, GET /api/health          │         │
│   │      └─ GET /api/events  (SSE for live updates)         │         │
│   │  • Sync engine (1 s tick, 10 retries, 10 min cooldown)  │         │
│   │  • Writes SQLite at %PROGRAMDATA%\PGL Attendance\       │         │
│   │  • Auto-start + auto-restart on crash (sc.exe failure)  │         │
│   └─────────────────────────────────────────────────────────┘         │
│                          ▲                                            │
│                          │  HTTP localhost                            │
│                          ▼                                            │
│   ┌─────────────────────────────────────────────────────────┐         │
│   │  PglAttendance.exe       (native WinForms desktop UI)   │         │
│   │  • Records grid, stats cards, sync buttons, settings    │         │
│   │  • Closing the window DOES NOT stop the service         │         │
│   └─────────────────────────────────────────────────────────┘         │
│                                                                       │
└───────────────────────────────────────────────────────────────────────┘
```

## What's preserved from the original NestJS backend

Behavior is byte-for-byte identical:

- **Device endpoint:** `POST /iclock/cdata` accepts any content type as text;
  `~DeviceName=` lines return literal `OK` without persisting; other lines
  get stored, parsed, and queued for sync.
- **Parser:** tab-separated, first four fields (userId, datetime, status, verifyType).
- **Real-time events** for new records, sync updates, stats updates. (NestJS used
  Socket.IO; we use SSE on `/api/events` — same semantics.)
- **Batch sort:** for batched POSTs, items are sorted as
  `[others by id asc] + [status=0 by datetime asc] + [status=1 by datetime asc]`.
- **Sync queue:** 1-second tick, FIFO, one record at a time.
- **HRMIS POST:** to `{hrmisUrl}/iclock/cdata`, only a literal `"OK"` response
  counts as success.
- **Retry:** up to **10 attempts**, then move to failed queue with **10-minute
  cooldown**, then re-queue with `retryCount` reset to 0.
- **SQLite schema:** same `RawAttendance` table (`id, rawData, isSynced,
  createdAt, retryCount, lastError`). The existing `dev.db` is binary-compatible
  if you ever want to import it.

## Build prerequisites

Install once on the Windows build machine:

| Tool          | Where                                                |
| ------------- | ---------------------------------------------------- |
| .NET 8 SDK    | <https://dotnet.microsoft.com/download/dotnet/8.0>   |
| Inno Setup 6  | <https://jrsoftware.org/isdl.php>                    |

Verify in PowerShell:
```powershell
dotnet --list-sdks      # 8.0.x
where.exe ISCC          # path to ISCC.exe
```

## Build command

```powershell
pwsh -ExecutionPolicy Bypass -File .\windows-build\scripts\build.ps1
# → windows-build\dist\PGL-Attendance-Setup-1.0.0.exe   (~45–60 MB)
```

Optional version:
```powershell
.\windows-build\scripts\build.ps1 -Version 1.2.0
```

The script:
1. `dotnet restore` the solution
2. `dotnet publish` Service exe (self-contained, single-file, win-x64)
3. `dotnet publish` Desktop exe (self-contained, single-file, win-x64)
4. Bootstraps an empty SQLite DB with schema applied (via the service exe itself)
5. Stages all files
6. Runs `ISCC.exe installer.iss` → final `.exe`

## What the installer does on the target PC

1. Drops `PglAttendanceService.exe`, `PglAttendance.exe`, `app.ico` into `C:\Program Files\PGL Attendance\`.
2. Seeds `C:\ProgramData\PGL Attendance\{settings.json, attendance.db, logs\}` (only on first install).
3. Registers Windows Service `PGLAttendanceSync` via `sc.exe create`:
   - `start= auto`
   - `failure reset= 60 actions= restart/3000/restart/3000/restart/5000`
4. Opens Windows Firewall inbound TCP **4001**.
5. `sc.exe start "PGLAttendanceSync"`.
6. Adds desktop shortcut + optional autostart at sign-in.
7. Launches `PglAttendance.exe` (desktop UI).

After install:
- Devices POST to `http://<PC_IP>:4001/iclock/cdata`.
- Dashboard window is the native desktop app — close it any time; service keeps running.
- Service starts at boot before any user logs in.

## Settings (changeable any time)

From the desktop UI's **Settings** button you can change:
- **HRMIS API URL** — hot-reloads, no restart needed.
- **Listening port** — the service gracefully self-stops; Windows SCM
  restarts it on the new port (firewall rule is added by the installer for
  4001; if you change the port, the rule needs updating manually — TODO).

Settings are stored at `C:\ProgramData\PGL Attendance\settings.json`. The
service watches this file with `FileSystemWatcher` and applies changes within
a quarter-second.

## Logs

`C:\ProgramData\PGL Attendance\logs\service.log` — rolling text log written by
the service. Also goes to the Windows Event Log under source `PGLAttendanceSync`.

## Upgrades

Higher-version installer detects the same `AppId` and upgrades in place:
1. Service stopped and unregistered
2. Files replaced
3. Service re-registered + started
4. SQLite DB and `settings.json` left untouched (uninstall doesn't delete them either)

## Uninstall

Standard "Apps & Features" → uninstall. Service is stopped + removed, firewall
rule deleted, desktop UI killed, `Program Files` folder removed. **The
`ProgramData` folder is left intact** so the attendance database survives.
