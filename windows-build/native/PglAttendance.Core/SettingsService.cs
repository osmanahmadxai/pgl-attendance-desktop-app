using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PglAttendance.Core;

/// <summary>
/// Patch for <see cref="SettingsService.Update"/> — every field is optional, and
/// null means "leave unchanged". Using a patch rather than a whole
/// <see cref="AppSettings"/> stops a caller that only knows about some fields
/// from silently resetting the others.
/// </summary>
public sealed record SettingsPatch(
    string? HrmisUrl = null,
    int? Port = null,
    bool? RemoteAccessEnabled = null,
    int? AdminHttpsPort = null,
    string[]? AllowedIps = null,
    string[]? DeviceAllowedIps = null,
    string? CertificatePath = null);

/// <summary>
/// File-backed settings store: loads JSON from
/// %PROGRAMDATA%\PGL Attendance\settings.json, watches the file for changes,
/// and fires events on change.
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

    /// <summary>
    /// Raised when a change requires Kestrel to rebind — the device port, the
    /// HTTPS port, or the remote-access switch. Listeners are fixed at startup,
    /// so the service restarts itself to apply these.
    /// </summary>
    public event Action<AppSettings, AppSettings>? ListenerChanged;

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

    public int AdminHttpsPort
    {
        get { lock (_lock) return _current.AdminHttpsPort; }
    }

    public bool RemoteAccessEnabled
    {
        get { lock (_lock) return _current.RemoteAccessEnabled; }
    }

    /// <summary>Backwards-compatible overload for the original two-field update.</summary>
    public AppSettings Update(string? hrmisUrl, int? port)
        => Update(new SettingsPatch(HrmisUrl: hrmisUrl, Port: port));

    public AppSettings Update(SettingsPatch patch)
    {
        AppSettings prev;
        AppSettings next;
        lock (_lock)
        {
            prev = _current.Clone();
            next = prev.Clone();

            if (!string.IsNullOrWhiteSpace(patch.HrmisUrl))
                next.HrmisUrl = patch.HrmisUrl.Trim().TrimEnd('/');
            if (patch.Port is int p && p > 0 && p < 65536)
                next.Port = p;
            if (patch.RemoteAccessEnabled is bool remote)
                next.RemoteAccessEnabled = remote;
            if (patch.AdminHttpsPort is int ap && ap > 0 && ap < 65536)
                next.AdminHttpsPort = ap;
            if (patch.AllowedIps is not null)
                next.AllowedIps = Clean(patch.AllowedIps);
            if (patch.DeviceAllowedIps is not null)
                next.DeviceAllowedIps = Clean(patch.DeviceAllowedIps);
            if (patch.CertificatePath is not null)
                next.CertificatePath = patch.CertificatePath.Trim();

            // The two ports must differ, otherwise Kestrel fails to bind and the
            // device listener would go down with the admin one.
            if (next.AdminHttpsPort == next.Port)
                next.AdminHttpsPort = prev.AdminHttpsPort == prev.Port ? next.Port + 1 : prev.AdminHttpsPort;

            WriteToDisk(next);
            _current = next;
        }
        FireChanged(prev, next);
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
        FireChanged(prev, next);
    }

    private void FireChanged(AppSettings prev, AppSettings next)
    {
        try { Changed?.Invoke(next, prev); } catch { /* ignore */ }

        var rebind = prev.Port != next.Port
                     || prev.AdminHttpsPort != next.AdminHttpsPort
                     || prev.RemoteAccessEnabled != next.RemoteAccessEnabled;
        if (rebind)
        {
            try { ListenerChanged?.Invoke(next, prev); } catch { /* ignore */ }
        }
    }

    private static string[] Clean(string[] values)
        => values.Select(v => (v ?? "").Trim())
                 .Where(v => v.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToArray();

    private static bool Equals(AppSettings a, AppSettings b)
        => string.Equals(a.HrmisUrl, b.HrmisUrl, StringComparison.Ordinal)
           && a.Port == b.Port
           && a.RemoteAccessEnabled == b.RemoteAccessEnabled
           && a.AdminHttpsPort == b.AdminHttpsPort
           && string.Equals(a.CertificatePath, b.CertificatePath, StringComparison.Ordinal)
           && a.AllowedIps.SequenceEqual(b.AllowedIps, StringComparer.OrdinalIgnoreCase)
           && a.DeviceAllowedIps.SequenceEqual(b.DeviceAllowedIps, StringComparer.OrdinalIgnoreCase);

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
                    s.RemoteAccessEnabled = parsed.RemoteAccessEnabled;
                    if (parsed.AdminHttpsPort > 0 && parsed.AdminHttpsPort < 65536)
                        s.AdminHttpsPort = parsed.AdminHttpsPort;
                    s.AllowedIps = Clean(parsed.AllowedIps ?? Array.Empty<string>());
                    s.DeviceAllowedIps = Clean(parsed.DeviceAllowedIps ?? Array.Empty<string>());
                    s.CertificatePath = (parsed.CertificatePath ?? "").Trim();
                }
            }
        }
        catch
        {
            // malformed file shouldn't take the service down; fall back to defaults/env
        }
        if (s.AdminHttpsPort == s.Port) s.AdminHttpsPort = s.Port + 1;
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
