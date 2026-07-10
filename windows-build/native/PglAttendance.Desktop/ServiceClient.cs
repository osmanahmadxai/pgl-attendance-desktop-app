using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PglAttendance.Core;
using PglAttendance.Core.Models;

namespace PglAttendance.Desktop;

public sealed class ServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public int Port { get; private set; } = 4001;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public void SetPort(int port) => Port = port;

    public sealed class HealthResp
    {
        public bool Ok { get; set; }
        public int Port { get; set; }
        public string HrmisUrl { get; set; } = "";
        public int UptimeSeconds { get; set; }
    }

    public async Task<HealthResp?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync($"{BaseUrl}/api/health", ct);
            if (!r.IsSuccessStatusCode) return null;
            var json = await r.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HealthResp
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                Port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pi) ? pi : Port,
                HrmisUrl = root.TryGetProperty("hrmisUrl", out var h) ? (h.GetString() ?? "") : "",
                UptimeSeconds = root.TryGetProperty("uptimeSeconds", out var u) && u.TryGetInt32(out var ui) ? ui : 0,
            };
        }
        catch { return null; }
    }

    public sealed class Page
    {
        public List<ParsedAttendanceVm> Data { get; set; } = new();
        public long Total { get; set; }
        public int PageNumber { get; set; }
        public int Limit { get; set; }
        public int TotalPages { get; set; }
    }

    public async Task<Page> GetAttendanceAsync(int page, int limit, string filter, string search = "", CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/attendance?page={page}&limit={limit}&filter={Uri.EscapeDataString(filter)}";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search)}";
        using var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        var json = await r.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var p = new Page
        {
            Total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tl) ? tl : 0,
            PageNumber = root.TryGetProperty("page", out var pp) && pp.TryGetInt32(out var pi) ? pi : page,
            Limit = root.TryGetProperty("limit", out var ll) && ll.TryGetInt32(out var li) ? li : limit,
            TotalPages = root.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var tpi) ? tpi : 0,
        };
        if (root.TryGetProperty("data", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                var vm = JsonSerializer.Deserialize<ParsedAttendanceVm>(el.GetRawText(), Json);
                if (vm != null) p.Data.Add(vm);
            }
        }
        return p;
    }

    public sealed class Stats { public long Total { get; set; } public long Synced { get; set; } public long Unsynced { get; set; } }

    public async Task<Stats?> GetStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync($"{BaseUrl}/stats", ct);
            r.EnsureSuccessStatusCode();
            var json = await r.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<Stats>(json, Json);
        }
        catch { return null; }
    }

    public async Task<bool> SyncAllAsync(CancellationToken ct = default)
    {
        try { using var r = await _http.PostAsync($"{BaseUrl}/sync-all", new StringContent("")); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<bool> SyncSelectedAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { ids });
            using var c = new StringContent(payload, Encoding.UTF8, "application/json");
            using var r = await _http.PostAsync($"{BaseUrl}/sync", c, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes every record in the local database. Returns the number of rows
    /// removed, or null if the service was unreachable or refused.
    /// </summary>
    public async Task<long?> DeleteAllAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.DeleteAsync($"{BaseUrl}/attendance", ct);
            if (!r.IsSuccessStatusCode) return null;
            var json = await r.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("deleted", out var d) && d.TryGetInt64(out var n) ? n : 0;
        }
        catch { return null; }
    }

    public sealed class CurrentSettings { public string HrmisUrl { get; set; } = ""; public int Port { get; set; } }

    public async Task<CurrentSettings?> GetSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync($"{BaseUrl}/api/settings", ct);
            r.EnsureSuccessStatusCode();
            var json = await r.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<CurrentSettings>(json, Json);
        }
        catch { return null; }
    }

    public async Task<bool> UpdateSettingsAsync(string hrmisUrl, int port, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { hrmisUrl, port });
            using var c = new StringContent(payload, Encoding.UTF8, "application/json");
            using var r = await _http.PutAsync($"{BaseUrl}/api/settings", c, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Streams Server-Sent Events from /api/events. Calls onEvent on each event.
    /// Returns when the connection ends or the token is cancelled.
    /// </summary>
    public async Task SubscribeEventsAsync(Action<string, string> onEvent, CancellationToken ct)
    {
        using var stream = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/events");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = await stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(s, Encoding.UTF8);
        string? evt = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                evt = null;
                continue;
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                evt = line.Substring(7).Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line.Substring(6);
                try { onEvent(evt ?? "message", data); } catch { /* swallow */ }
            }
            // lines starting with ":" are comments/heartbeats — ignore
        }
    }
}
