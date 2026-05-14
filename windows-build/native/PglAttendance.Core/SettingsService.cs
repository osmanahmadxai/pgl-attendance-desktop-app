using System;
using System.IO;
using System.Text.Json;

namespace PglAttendance.Core;

/// <summary>
/// File-backed settings store mirroring NestJS SettingsService:
/// loads JSON from %PROGRAMDATA%\PGL Attendance\settings.json,
/// watches the file for changes, fires events on change.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private AppSettings _current;

    public event Action<AppSettings, AppSettings>? Changed;
    public event Action<int, int>? PortChanged;

    public SettingsService()
    {
        _current = LoadFromDisk();
    }

    public AppSettings Get()
    {
        lock (_lock) return _current.Clone();
    }

    public string HrmisUrl
    {
        get
        {
            lock (_lock) return (_current.HrmisUrl ?? "").TrimEnd('/');
        }
    }

    public int Port
    {
        get { lock (_lock) return _current.Port; }
    }

    public AppSettings Update(string? hrmisUrl, int? port)
    {
        AppSettings prev;
        AppSettings next;
        lock (_lock)
        {
            prev = _current.Clone();
            next = prev.Clone();
            if (!string.IsNullOrWhiteSpace(hrmisUrl))
                next.HrmisUrl = hrmisUrl.Trim().TrimEnd('/');
            if (port.HasValue && port.Value > 0 && port.Value < 65536)
                next.Port = port.Value;

            WriteToDisk(next);
            _current = next;
        }
        FireChanged(prev, next, "api");
        return next.Clone();
    }

    public void Start()
    {
        Paths.EnsureDirs();
        if (!File.Exists(Paths.SettingsFile))
        {
            try { WriteToDisk(_current); } catch { /* ignore */ }
        }
        if (_watcher != null) return;
        _watcher = new FileSystemWatcher(Paths.DataDir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (_, _) => OnFileChanged(null!, null!);
    }

    private void OnFileChanged(object _, FileSystemEventArgs __)
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => ReloadFromDisk(), null, 250, System.Threading.Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        AppSettings prev;
        AppSettings next;
        lock (_lock)
        {
            prev = _current.Clone();
            next = LoadFromDisk();
            if (Equals(prev, next)) return;
            _current = next;
        }
        FireChanged(prev, next, "disk");
    }

    private void FireChanged(AppSettings prev, AppSettings next, string source)
    {
        try { Changed?.Invoke(next, prev); } catch { /* ignore */ }
        if (prev.Port != next.Port)
        {
            try { PortChanged?.Invoke(next.Port, prev.Port); } catch { /* ignore */ }
        }
    }

    private static bool Equals(AppSettings a, AppSettings b)
        => string.Equals(a.HrmisUrl, b.HrmisUrl, StringComparison.Ordinal) && a.Port == b.Port;

    private static AppSettings LoadFromDisk()
    {
        var s = new AppSettings();
        var envUrl = Environment.GetEnvironmentVariable("HRMIS_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) s.HrmisUrl = envUrl;
        var envPort = Environment.GetEnvironmentVariable("PORT");
        if (int.TryParse(envPort, out var ep) && ep > 0 && ep < 65536) s.Port = ep;
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var json = File.ReadAllText(Paths.SettingsFile);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json);
                if (parsed != null)
                {
                    if (!string.IsNullOrWhiteSpace(parsed.HrmisUrl))
                        s.HrmisUrl = parsed.HrmisUrl.TrimEnd('/');
                    if (parsed.Port > 0 && parsed.Port < 65536) s.Port = parsed.Port;
                }
            }
        }
        catch
        {
            // malformed file shouldn't take the service down; fall back to defaults/env
        }
        return s;
    }

    private static void WriteToDisk(AppSettings s)
    {
        Paths.EnsureDirs();
        var json = JsonSerializer.Serialize(s, JsonOpts);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
        File.Move(tmp, Paths.SettingsFile);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }
}
