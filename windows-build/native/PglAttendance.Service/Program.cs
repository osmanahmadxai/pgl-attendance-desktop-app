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
using PglAttendance.Core.Security;
using PglAttendance.Core.Sync;
using PglAttendance.Service.Security;
using PglAttendance.Service.Sync;
using PglAttendance.Service.Web;

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
if (OperatingSystem.IsWindows())
{
    AddWindowsEventLog(builder.Logging);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void AddWindowsEventLog(Microsoft.Extensions.Logging.ILoggingBuilder logging)
{
    logging.AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings
    {
        SourceName = "PGLAttendanceSync",
        LogName = "Application",
    });
}
// File logging — write to ProgramData\logs so users can read without admin
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(Paths.LogDir, "service.log")));

// ---------------------------------------------------------------------------
// Listener layout — the backbone of both the security and the stability story.
//
//   device port (HTTP, always)   : /iclock/* to the network; full API to loopback
//                                  only, which is how the desktop app keeps
//                                  working exactly as before with no login.
//   admin port  (HTTPS, optional): browser UI + API, always authenticated.
//
// The admin listener is only created when remote access is switched on AND a
// certificate could be produced, so when it is off the surface does not exist
// at the socket level — not merely behind an auth check.
// ---------------------------------------------------------------------------
var bootConfig = bootSettings.Get();
var credentials = new CredentialStore();
var securityState = new SecurityState(credentials);

System.Security.Cryptography.X509Certificates.X509Certificate2? adminCert = null;
string? adminCertError = null;
var adminSelfSigned = false;

if (bootConfig.RemoteAccessEnabled)
{
    if (!credentials.IsConfigured)
    {
        adminCertError = "no administrator account is set";
    }
    else
    {
        var certResult = CertificateProvider.Load(
            string.IsNullOrWhiteSpace(bootConfig.CertificatePath) ? null : bootConfig.CertificatePath,
            credentials.CertificatePassword);
        adminCert = certResult.Certificate;
        adminCertError = certResult.Error;
        adminSelfSigned = certResult.SelfSigned;
    }
}

var remoteActive = bootConfig.RemoteAccessEnabled && adminCert is not null;

builder.WebHost.UseKestrel(opts =>
{
    opts.AddServerHeader = false;
    opts.ListenAnyIP(port);

    if (remoteActive)
    {
        opts.ListenAnyIP(bootConfig.AdminHttpsPort, lo => lo.UseHttps(adminCert!));
    }
});

builder.Services.AddSingleton(bootSettings);
builder.Services.AddSingleton(credentials);
builder.Services.AddSingleton(securityState);
builder.Services.AddSingleton<AttendanceRepository>(_ => new AttendanceRepository());
builder.Services.AddSingleton<HrmisClient>();
builder.Services.AddSingleton<RealtimeBroadcaster>();
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncEngine>());

// No CORS policy on purpose: the browser UI is served from the same origin as
// the API, so cross-origin access should simply be impossible. The previous
// AllowAnyOrigin policy would have let any web page on the network script the
// API on behalf of a logged-in admin.

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

// Access control runs before everything else, so no endpoint below can be
// reached without passing through it.
app.UseAccessControl(bootSettings, securityState);

// Browser UI, embedded in this assembly. Reachable only through the gate above.
app.UseEmbeddedUi();

// ---------------------------------------------------------------------------
// Verbose request logger for everything under /iclock/* — captures device
// pings/handshakes/data so we can see exactly what's hitting us.
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/iclock", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Request.EnableBuffering();
        string body = "";
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;
        }
        catch { }

        var lg = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("iclock");
        lg.LogInformation(
            "DEVICE  {Method} {Path}{Query}  from {Ip}  body[{Len} B]: {Body}",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.Request.QueryString,
            ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            body.Length,
            body.Length > 400 ? body.Substring(0, 400) + "..." : body);
    }
    await next();
});

// ---------------------------------------------------------------------------
// Device handshake — ZK iClock devices GET here on boot to discover the
// server's expected push schedule.  Without this they may refuse to POST data.
// ---------------------------------------------------------------------------
app.MapGet("/iclock/cdata", (HttpRequest req) =>
{
    var sn = req.Query["SN"].ToString();
    // Minimal ZK-compatible "OPTIONS" reply.  Realtime=1 -> push data live.
    var reply =
        $"GET OPTION FROM: {sn}\n" +
        "Stamp=9999\n" +
        "OpStamp=0\n" +
        "ErrorDelay=30\n" +
        "Delay=10\n" +
        "TransTimes=00:00;14:05\n" +
        "TransInterval=1\n" +
        "TransFlag=TransData AttLog OpLog AttPhoto EnrollUser ChgUser EnrollFP ChgFP UserPic\n" +
        "Realtime=1\n" +
        "Encrypt=None\n";
    return Results.Text(reply, "text/plain");
});

// Devices poll this for queued commands.  We never queue anything -> empty body.
app.MapGet("/iclock/getrequest", () => Results.Text("OK", "text/plain"));

// Devices report command execution results back here.  Acknowledge.
app.MapPost("/iclock/devicecmd", () => Results.Text("OK", "text/plain"));

// ---------------------------------------------------------------------------
// Device data endpoint: POST /iclock/cdata
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
// GET /attendance?page=&limit=&filter=&search=
// search matches anywhere in the raw record, across the entire database.
// ---------------------------------------------------------------------------
app.MapGet("/attendance", async (
    [FromServices] AttendanceRepository repo,
    int? page, int? limit, string? filter, string? search) =>
{
    var p = await repo.GetAttendanceAsync(page ?? 1, limit ?? 10, filter ?? "all", search);
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
// DELETE /attendance — wipe the local database (records are already in HRMIS
// once synced; the desktop UI confirms with the user before calling this).
// Queue is cleared first so nothing references a deleted row.
// ---------------------------------------------------------------------------
app.MapDelete("/attendance", async (
    [FromServices] SyncEngine engine,
    [FromServices] AttendanceRepository repo,
    [FromServices] RealtimeBroadcaster realtime,
    [FromServices] ILoggerFactory lf) =>
{
    engine.ClearQueue();
    var deleted = await repo.DeleteAllAsync();
    realtime.EmitStatsUpdate();
    lf.CreateLogger("Attendance").LogInformation("Deleted all local attendance data ({Count} rows)", deleted);
    return Results.Json(new { success = true, deleted });
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
// Debug: GET /api/debug/recent?limit=N  -> raw rows, no filter.
// Useful when device data arrives but doesn't show up because of the
// "OPLOG / requires-tab" filter the main /attendance query applies.
// ---------------------------------------------------------------------------
app.MapGet("/api/debug/recent", async ([FromServices] AttendanceRepository repo, int? limit) =>
{
    var rows = await repo.GetRecentRawAsync(limit ?? 100);
    return Results.Json(rows);
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
app.MapGet("/api/settings", ([FromServices] SettingsService settings, [FromServices] SecurityState sec) =>
{
    var s = settings.Get();
    return Results.Json(new
    {
        hrmisUrl = s.HrmisUrl,
        port = s.Port,
        remoteAccessEnabled = s.RemoteAccessEnabled,
        adminHttpsPort = s.AdminHttpsPort,
        allowedIps = s.AllowedIps,
        deviceAllowedIps = s.DeviceAllowedIps,
        certificatePath = s.CertificatePath,
        // Never the password or its hash — only whether an account exists.
        accountConfigured = sec.Credentials.IsConfigured,
        username = sec.Credentials.Username,
        remoteAccessActive = remoteActive,
        certificateError = adminCertError,
        certificateSelfSigned = adminSelfSigned,
        certificateFingerprint = adminCert is null ? null : CertificateProvider.Fingerprint(adminCert),
        minPasswordLength = CredentialStore.MinPasswordLength,
    });
});

app.MapPut("/api/settings", async (
    [FromServices] SettingsService settings,
    [FromServices] SecurityState sec,
    [FromBody] UpdateSettingsDto body) =>
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
    if (body.AdminHttpsPort is not null && (body.AdminHttpsPort < 1 || body.AdminHttpsPort > 65535))
        return Results.BadRequest(new { message = "adminHttpsPort must be between 1 and 65535" });
    if (body.Port is not null && body.AdminHttpsPort is not null && body.Port == body.AdminHttpsPort)
        return Results.BadRequest(new { message = "the device port and the browser port must be different" });

    foreach (var rule in (body.AllowedIps ?? Array.Empty<string>()).Concat(body.DeviceAllowedIps ?? Array.Empty<string>()))
    {
        if (!string.IsNullOrWhiteSpace(rule) && !IpAllowList.IsValidRule(rule))
            return Results.BadRequest(new { message = $"'{rule}' is not a valid IP address or CIDR range" });
    }

    // Refusing to open the browser surface without an account is what stops a
    // toggle from ever exposing an unauthenticated API.
    if (body.RemoteAccessEnabled == true && !sec.Credentials.IsConfigured)
        return Results.BadRequest(new { message = "Set an administrator username and password before enabling browser access." });

    if (body.CertificatePassword is not null)
        sec.Credentials.SetCertificatePassword(body.CertificatePassword);

    var next = settings.Update(new SettingsPatch(
        HrmisUrl: body.HrmisUrl,
        Port: body.Port,
        RemoteAccessEnabled: body.RemoteAccessEnabled,
        AdminHttpsPort: body.AdminHttpsPort,
        AllowedIps: body.AllowedIps,
        DeviceAllowedIps: body.DeviceAllowedIps,
        CertificatePath: body.CertificatePath));

    await Task.CompletedTask;
    return Results.Json(new { ok = true, settings = next });
});

// ---------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------
app.MapGet("/api/auth/status", (HttpContext ctx, [FromServices] SecurityState sec) =>
{
    var token = ctx.Request.Cookies[AccessControl.SessionCookie];
    var user = sec.Sessions.Validate(token, sec.Credentials.SecurityStamp);
    return Results.Json(new
    {
        configured = sec.Credentials.IsConfigured,
        authenticated = user is not null,
        username = user,
        minPasswordLength = CredentialStore.MinPasswordLength,
    });
});

app.MapPost("/api/auth/login", async (
    HttpContext ctx,
    [FromServices] SecurityState sec,
    [FromServices] ILoggerFactory lf,
    [FromBody] LoginDto body) =>
{
    var log = lf.CreateLogger("Security");
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var username = (body?.Username ?? "").Trim();

    // Throttle on the address and the account independently, so neither
    // spraying one password across accounts nor many passwords from one host
    // stays viable.
    var wait = sec.Throttle.RetryAfterAny($"ip:{ip}", $"user:{username}");
    if (wait is not null)
    {
        log.LogWarning("Login for '{User}' from {Ip} blocked — locked out for another {Seconds}s.",
            username, ip, (int)wait.Value.TotalSeconds);
        ctx.Response.Headers.RetryAfter = ((int)wait.Value.TotalSeconds).ToString();
        return Results.Json(new { error = "Too many failed attempts. Try again later." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (body is null || !sec.Credentials.Validate(username, body.Password ?? ""))
    {
        sec.Throttle.RecordFailure($"ip:{ip}");
        sec.Throttle.RecordFailure($"user:{username}");
        log.LogWarning("Failed login for '{User}' from {Ip}.", username, ip);
        await Task.Delay(250); // blunt the timing signal on repeated probing
        return Results.Json(new { error = "Invalid username or password." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    sec.Throttle.RecordSuccess($"ip:{ip}");
    sec.Throttle.RecordSuccess($"user:{username}");
    var token = sec.Sessions.Create(sec.Credentials.Username, sec.Credentials.SecurityStamp);
    AccessControl.SetSessionCookie(ctx, token);
    log.LogInformation("Successful login for '{User}' from {Ip}.", sec.Credentials.Username, ip);
    return Results.Json(new { ok = true, username = sec.Credentials.Username });
});

app.MapPost("/api/auth/logout", (HttpContext ctx, [FromServices] SecurityState sec) =>
{
    sec.Sessions.Revoke(ctx.Request.Cookies[AccessControl.SessionCookie]);
    AccessControl.ClearSessionCookie(ctx);
    return Results.Json(new { ok = true });
});

// Change (or first-time set) the administrator account. Reachable from the
// desktop app over loopback and from an authenticated browser session; both
// must supply the current password once one exists.
app.MapPost("/api/auth/password", (
    HttpContext ctx,
    [FromServices] SecurityState sec,
    [FromServices] ILoggerFactory lf,
    [FromBody] PasswordDto body) =>
{
    if (body is null) return Results.BadRequest(new { message = "Body required" });

    var log = lf.CreateLogger("Security");
    var username = string.IsNullOrWhiteSpace(body.Username) ? sec.Credentials.Username : body.Username;

    var result = sec.Credentials.SetCredentials(username, body.NewPassword, body.CurrentPassword);
    if (!result.Ok)
    {
        log.LogWarning("Rejected credential change from {Ip}: {Reason}",
            ctx.Connection.RemoteIpAddress, result.Error);
        return Results.BadRequest(new { message = result.Error });
    }

    // Every existing session was issued under the old security stamp, so this
    // logs out every browser everywhere — including an attacker's.
    sec.Sessions.RevokeAll();
    AccessControl.ClearSessionCookie(ctx);
    log.LogInformation("Administrator credentials updated from {Ip}; all sessions revoked.",
        ctx.Connection.RemoteIpAddress);
    return Results.Json(new { ok = true, username = sec.Credentials.Username });
});

// Hot-reload + self-exit when a listener-affecting setting changes, so the
// Windows SCM restarts the service on the new ports.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
bootSettings.ListenerChanged += (next, prev) =>
{
    var lg = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Settings");
    lg.LogInformation(
        "Listener settings changed (device {PrevPort}->{NextPort}, browser {PrevRemote}:{PrevHttps}->{NextRemote}:{NextHttps}); restarting.",
        prev.Port, next.Port, prev.RemoteAccessEnabled, prev.AdminHttpsPort, next.RemoteAccessEnabled, next.AdminHttpsPort);
    FirewallManager.Reconcile(next, lg);
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

// Keep the firewall in step with the configured ports — the installer can only
// ever open the port it knew about at install time.
FirewallManager.Reconcile(bootConfig, app.Logger);

app.Logger.LogInformation("PGLAttendanceSync service listening on port {Port} (device); {Assets} UI assets embedded", port, EmbeddedUi.Count);
if (remoteActive)
{
    app.Logger.LogInformation(
        "Browser access enabled on https://<this-pc>:{Port} ({Kind} certificate, SHA-256 {Fingerprint})",
        bootConfig.AdminHttpsPort,
        adminSelfSigned ? "self-signed" : "supplied",
        CertificateProvider.Fingerprint(adminCert!));
    var allow = bootConfig.AllowedIps.Length == 0 ? "any address" : string.Join(", ", bootConfig.AllowedIps);
    app.Logger.LogInformation("Browser access allow-list: {Allow}", allow);
}
else if (bootConfig.RemoteAccessEnabled)
{
    // Remote access was requested but could not be brought up. The device
    // listener is unaffected — attendance collection must never depend on the
    // admin surface starting successfully.
    app.Logger.LogError(
        "Browser access is enabled in settings but could NOT start: {Reason}. Attendance collection is unaffected.",
        adminCertError ?? "unknown error");
}
else
{
    app.Logger.LogInformation("Browser access is disabled; the API is reachable only from this computer.");
}

app.Run();

// ---------------------------------------------------------------------------
public sealed record SyncIdsDto(long[]? Ids);

public sealed record UpdateSettingsDto(
    string? HrmisUrl,
    int? Port,
    bool? RemoteAccessEnabled,
    int? AdminHttpsPort,
    string[]? AllowedIps,
    string[]? DeviceAllowedIps,
    string? CertificatePath,
    string? CertificatePassword);

public sealed record LoginDto(string? Username, string? Password);

public sealed record PasswordDto(string? Username, string? CurrentPassword, string? NewPassword);

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
