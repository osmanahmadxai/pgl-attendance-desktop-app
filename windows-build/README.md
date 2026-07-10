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
│   │      └─ GET  /attendance, /stats, DELETE /attendance …  │         │
│   │      └─ GET/PUT /api/settings, GET /api/health          │         │
│   │      └─ GET /api/events  (SSE for live updates)         │         │
│   │  • Sync engine (exactly-once, oldest punch first)       │         │
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

## Sync engine guarantees

- **Verbatim payloads:** each record line is stored and POSTed to
  `{hrmisUrl}/iclock/cdata` exactly as the device sent it — no timezone
  conversion or rewriting anywhere. Punch timestamps are parsed only to decide
  send order.
- **Exactly-once:** identical device re-uploads collapse into one row (UNIQUE
  index on `rawData`, with a startup migration that dedupes legacy rows), a
  record id can be queued only once, and `isSynced` is re-checked immediately
  before every POST. Clicking "Sync all" repeatedly can never double-send.
- **Oldest first:** the queue is a priority queue keyed by the punch timestamp
  inside the record, so records always go out in chronological order — HRMIS
  pairs check-outs against previously received check-ins, so this matters.
- **Order-preserving retries:** if HRMIS is unreachable (network error /
  5xx / 408 / 429), the same record retries at the head with exponential
  backoff (2 s → 5 min) and later records never overtake it. A record HRMIS
  actively rejects (4xx / non-`OK` body) is marked with `lastError` and
  skipped so it can't block the queue; a record that keeps drawing server
  errors is skipped after 10 answered failures. Both stay Pending and can be
  retried with "Sync all".
- **Crash-safe:** on service start every unsynced row is re-queued
  automatically (in the background, so startup is never delayed).
- **Device endpoint:** `POST /iclock/cdata` accepts any content type as text;
  `~DeviceName=` lines return literal `OK` without persisting; `OPLOG` rows
  and records HRMIS wouldn't store are kept local and excluded from sync.
- **Real-time events** for new records, sync updates, stats updates over SSE
  on `/api/events`.
- **Delete all data:** `DELETE /attendance` (desktop button with confirmation)
  wipes the local SQLite table; ids are never reused.
- **Global search:** `GET /attendance?search=` matches the entire database —
  all-digit queries match the user id prefix, anything else is a substring
  match (dates, times).
- **SQLite schema:** same `RawAttendance` table (`id, rawData, isSynced,
  createdAt, retryCount, lastError`), so an existing database is compatible;
  duplicates left by older versions are cleaned up on first start.

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
