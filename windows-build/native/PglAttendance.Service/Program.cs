using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PglAttendance.Core;
using PglAttendance.Core.Data;
using PglAttendance.Core.Sync;
using PglAttendance.Service.Sync;

Paths.EnsureDirs();

// Seed settings on first run + load initial port.
using (var seed = new SettingsService()) { /* initialize file on first run */ }
var bootSettings = new SettingsService();
bootSettings.Start();
var port = bootSettings.Port;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Run-as-Windows-Service when launched by the SCM.
builder.Host.UseWindowsService(options => options.ServiceName = "PGLAttendanceSync");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddEventLog(eventLogSettings =>
{
    eventLogSettings.SourceName = "PGLAttendanceSync";
    eventLogSettings.LogName = "Application";
});
// File logging — write to ProgramData\logs so users can read without admin
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(Paths.LogDir, "service.log")));

builder.WebHost.UseKestrel(opts =>
{
    opts.ListenAnyIP(port);
});

builder.Services.AddSingleton(bootSettings);
builder.Services.AddSingleton<AttendanceRepository>(_ => new AttendanceRepository());
builder.Services.AddSingleton<HrmisClient>();
builder.Services.AddSingleton<RealtimeBroadcaster>();
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncEngine>());

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithMethods("GET", "POST", "PUT", "OPTIONS"));
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var lg = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HTTP");
        lg.LogError(ex, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("SERVER ERROR");
        }
    }
});

app.UseCors();

// ---------------------------------------------------------------------------
// Device endpoint: POST /iclock/cdata
// Body: any content type, treated as plain text. Returns literal "OK".
// ---------------------------------------------------------------------------
app.MapPost("/iclock/cdata", async (HttpRequest req, SyncEngine engine) =>
{
    string raw;
    using (var sr = new StreamReader(req.Body, Encoding.UTF8))
        raw = (await sr.ReadToEndAsync()).Trim();

    if (raw.StartsWith("~DeviceName=", StringComparison.Ordinal))
    {
        // NestJS: log + return OK without saving
        Console.WriteLine($"Device info received: {raw}");
        return Results.Text("OK", "text/plain");
    }
    await engine.SaveAttendanceAsync(raw);
    return Results.Text("OK", "text/plain");
});

// ---------------------------------------------------------------------------
// GET /attendance?page=&limit=&filter=
// ---------------------------------------------------------------------------
app.MapGet("/attendance", async (
    [FromServices] AttendanceRepository repo,
    int? page, int? limit, string? filter) =>
{
    var p = await repo.GetAttendanceAsync(page ?? 1, limit ?? 10, filter ?? "all");
    return Results.Json(new
    {
        data = p.Data,
        total = p.Total,
        page = p.PageNumber,
        limit = p.Limit,
        totalPages = p.TotalPages,
    });
});

// ---------------------------------------------------------------------------
// GET /unsynced-ids
// ---------------------------------------------------------------------------
app.MapGet("/unsynced-ids", async ([FromServices] AttendanceRepository repo) =>
{
    var (ids, count) = await repo.GetAllUnsyncedIdsAsync();
    return Results.Json(new { ids, count });
});

// ---------------------------------------------------------------------------
// POST /sync   body: { ids: number[] }
// ---------------------------------------------------------------------------
app.MapPost("/sync", async ([FromServices] SyncEngine engine, SyncIdsDto body) =>
{
    var r = await engine.SyncSelectedAsync(body.Ids ?? Array.Empty<long>());
    return Results.Json(new { success = r.Success, message = r.Message });
});

// ---------------------------------------------------------------------------
// POST /sync-all
// ---------------------------------------------------------------------------
app.MapPost("/sync-all", async ([FromServices] SyncEngine engine) =>
{
    var r = await engine.SyncAllRecordsAsync();
    return Results.Json(new { success = r.Success, message = r.Message });
});

// ---------------------------------------------------------------------------
// GET /stats
// ---------------------------------------------------------------------------
app.MapGet("/stats", async ([FromServices] AttendanceRepository repo) =>
{
    var s = await repo.GetStatsAsync();
    return Results.Json(new { total = s.Total, synced = s.Synced, unsynced = s.Unsynced });
});

// ---------------------------------------------------------------------------
// GET /api/health
// ---------------------------------------------------------------------------
var startedAt = DateTime.UtcNow;
app.MapGet("/api/health", ([FromServices] SettingsService settings) =>
{
    var s = settings.Get();
    return Results.Json(new
    {
        ok = true,
        pid = Environment.ProcessId,
        uptimeSeconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds,
        port = s.Port,
        hrmisUrl = s.HrmisUrl,
        now = DateTime.UtcNow.ToString("o"),
    });
});

// ---------------------------------------------------------------------------
// GET /api/settings   PUT /api/settings
// ---------------------------------------------------------------------------
app.MapGet("/api/settings", ([FromServices] SettingsService settings) => Results.Json(settings.Get()));
app.MapPut("/api/settings", async ([FromServices] SettingsService settings, [FromBody] UpdateSettingsDto body) =>
{
    if (body is null) return Results.BadRequest(new { message = "Body required" });
    if (body.HrmisUrl is not null)
    {
        if (string.IsNullOrWhiteSpace(body.HrmisUrl))
            return Results.BadRequest(new { message = "hrmisUrl must be a non-empty string" });
        if (!Uri.TryCreate(body.HrmisUrl, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return Results.BadRequest(new { message = "hrmisUrl must be a valid http(s) URL" });
    }
    if (body.Port is not null && (body.Port < 1 || body.Port > 65535))
        return Results.BadRequest(new { message = "port must be between 1 and 65535" });

    var next = settings.Update(body.HrmisUrl, body.Port);
    await Task.CompletedTask;
    return Results.Json(new { ok = true, settings = next });
});

// Hot-reload + self-exit on port change so Windows SCM restarts on the new port.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
bootSettings.PortChanged += (next, prev) =>
{
    var lg = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Settings");
    lg.LogInformation("Port changed {Prev} -> {Next}, shutting down for SCM restart.", prev, next);
    _ = Task.Run(async () =>
    {
        await Task.Delay(1500);
        lifetime.StopApplication();
    });
};

// ---------------------------------------------------------------------------
// GET /api/events  — Server-Sent Events stream for the desktop UI
// ---------------------------------------------------------------------------
app.MapGet("/api/events", async (HttpContext ctx, RealtimeBroadcaster bus) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.Body.FlushAsync();

    using var sem = new SemaphoreSlim(0, int.MaxValue);
    var cq = new System.Collections.Concurrent.ConcurrentQueue<(string evt, string data)>();
    void Handler(string evt, string data) { cq.Enqueue((evt, data)); sem.Release(); }
    var id = bus.Subscribe(Handler);

    try
    {
        // initial hello
        await ctx.Response.WriteAsync(":ok\n\n");
        await ctx.Response.Body.FlushAsync();

        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            // wake every 15s to send a heartbeat comment so the connection stays alive
            var waited = await sem.WaitAsync(TimeSpan.FromSeconds(15), ctx.RequestAborted);
            if (!waited)
            {
                await ctx.Response.WriteAsync(":hb\n\n");
                await ctx.Response.Body.FlushAsync();
                continue;
            }
            while (cq.TryDequeue(out var msg))
            {
                await ctx.Response.WriteAsync($"event: {msg.evt}\ndata: {msg.data}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally { bus.Unsubscribe(id); }
});

app.Logger.LogInformation("PGLAttendanceSync service listening on port {Port}", port);
app.Run();

// ---------------------------------------------------------------------------
public sealed record SyncIdsDto(long[]? Ids);
public sealed record UpdateSettingsDto(string? HrmisUrl, int? Port);

// ---------------------------------------------------------------------------
// Tiny rolling-friendly file logger so the service writes to ProgramData\logs.
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    public FileLoggerProvider(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, _lock, categoryName);
    public void Dispose() { }
    private sealed class FileLogger : ILogger
    {
        private readonly string _path;
        private readonly object _lock;
        private readonly string _cat;
        public FileLogger(string p, object l, string c) { _path = p; _lock = l; _cat = c; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(l)) return;
            var line = $"[{DateTime.Now:O}] [{l}] {_cat}: {fmt(s, ex)}";
            if (ex != null) line += " | " + ex;
            lock (_lock)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); } catch { /* ignore */ }
            }
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
