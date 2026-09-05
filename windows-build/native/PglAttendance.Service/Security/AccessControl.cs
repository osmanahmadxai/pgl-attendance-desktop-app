using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PglAttendance.Core;
using PglAttendance.Core.Security;

namespace PglAttendance.Service.Security;

/// <summary>
/// Shared authentication state, registered as a singleton.
/// </summary>
public sealed class SecurityState
{
    public SecurityState(CredentialStore credentials)
    {
        Credentials = credentials;
    }

    public CredentialStore Credentials { get; }
    public SessionStore Sessions { get; } = new();
    public LoginThrottle Throttle { get; } = new();
}

/// <summary>
/// The single gate every request passes through.
///
/// Which surface a request arrived on is decided by the local port it landed
/// on, not by anything the caller can influence:
///
///   Device port (HTTP)
///     • from loopback  → unrestricted; this is the desktop app, which the
///       user explicitly wants to work without a login
///     • from elsewhere → only /iclock/*, optionally IP-restricted. Anything
///       else returns 404, so a remote scanner cannot even tell the management
///       API exists on this port.
///
///   Admin port (HTTPS)
///     • IP allow-list first, then a valid session for everything except the
///       login endpoints and the login page itself.
///
/// Ordering matters: this runs before routing, so no endpoint can be reached
/// without passing through it.
/// </summary>
public static class AccessControl
{
    public const string SessionCookie = "pgl_session";

    /// <summary>
    /// Browsers cannot send this header cross-origin without a preflight that
    /// same-origin policy will refuse, so requiring it on state-changing calls
    /// blocks CSRF even if SameSite were somehow bypassed.
    /// </summary>
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "PglAttendance";

    /// <summary>Paths on the admin port reachable without a session.</summary>
    private static bool IsPublicAdminPath(PathString path)
        => path.Equals("/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/login.css", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/login.js", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase);

    public static void UseAccessControl(this WebApplication app, SettingsService settings, SecurityState security)
    {
        app.Use(async (ctx, next) =>
        {
            var current = settings.Get();
            var remoteIp = ctx.Connection.RemoteIpAddress;
            var isLoopback = remoteIp is not null && IPAddress.IsLoopback(remoteIp);
            var onAdminPort = current.RemoteAccessEnabled && ctx.Connection.LocalPort == current.AdminHttpsPort;

            if (onAdminPort)
            {
                await HandleAdminSurface(ctx, current, security, remoteIp, next);
                return;
            }

            // ---- device port -------------------------------------------------
            if (isLoopback)
            {
                // The desktop app on this machine: unchanged behaviour.
                await next();
                return;
            }

            var isDevicePath = ctx.Request.Path.StartsWithSegments("/iclock", StringComparison.OrdinalIgnoreCase);
            if (!isDevicePath)
            {
                // Do not reveal that a management API lives here.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var deviceAllow = new IpAllowList(current.DeviceAllowedIps);
            if (!deviceAllow.IsAllowed(remoteIp))
            {
                var lg = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Security");
                lg.LogWarning("Device POST from {Ip} rejected — not in the device allow-list.", remoteIp);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });
    }

    private static async Task HandleAdminSurface(
        HttpContext ctx,
        AppSettings current,
        SecurityState security,
        IPAddress? remoteIp,
        Func<Task> next)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Security");

        ApplySecurityHeaders(ctx);

        var allow = new IpAllowList(current.AllowedIps);
        if (!allow.IsAllowed(remoteIp))
        {
            logger.LogWarning("Browser request from {Ip} rejected — not in the allow-list.", remoteIp);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Forbidden");
            return;
        }

        // Without credentials there is nothing to authenticate against, so the
        // whole surface stays shut rather than falling open.
        if (!security.Credentials.IsConfigured)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("Remote access is not configured yet. Set a username and password in the desktop app.");
            return;
        }

        if (IsPublicAdminPath(ctx.Request.Path))
        {
            await next();
            return;
        }

        var token = ctx.Request.Cookies[SessionCookie];
        var user = security.Sessions.Validate(token, security.Credentials.SecurityStamp);
        if (user is null)
        {
            if (WantsHtml(ctx))
            {
                ctx.Response.Redirect("/login");
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "authentication required" });
            return;
        }

        if (IsStateChanging(ctx.Request.Method)
            && !string.Equals(ctx.Request.Headers[CsrfHeader], CsrfValue, StringComparison.Ordinal))
        {
            logger.LogWarning("Rejected {Method} {Path} from {Ip} — missing CSRF header.",
                ctx.Request.Method, ctx.Request.Path, remoteIp);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "missing request header" });
            return;
        }

        ctx.Items["user"] = user;
        await next();
    }

    private static bool IsStateChanging(string method)
        => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool WantsHtml(HttpContext ctx)
        => ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static void ApplySecurityHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        // Everything the UI needs is served from this origin; no CDNs, no inline
        // event handlers, nothing embeddable.
        h["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cache-Control"] = "no-store";
        h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    }

    /// <summary>Issues the session cookie with the strictest attributes the browser offers.</summary>
    public static void SetSessionCookie(HttpContext ctx, string token)
    {
        ctx.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = SessionStore.AbsoluteTimeout,
        });
    }

    public static void ClearSessionCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Append(SessionCookie, "", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UnixEpoch,
        });
    }
}
