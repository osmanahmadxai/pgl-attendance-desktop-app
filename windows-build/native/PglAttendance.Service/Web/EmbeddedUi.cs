using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PglAttendance.Service.Web;

/// <summary>
/// Serves the browser UI from resources embedded in the assembly rather than
/// from loose files on disk.
///
/// This matters because the service is published as a self-contained
/// single-file executable and the installer stages exactly one file
/// (PglAttendanceService.exe). A wwwroot folder next to it would simply never
/// be copied, so the UI would 404 in production while working perfectly in a
/// development run. Embedding removes that whole class of failure, and it also
/// means the pages cannot be altered on disk on the host PC.
///
/// Assets are read once at startup and held in memory — a handful of small
/// text files.
/// </summary>
public static class EmbeddedUi
{
    private const string ResourcePrefix = "PglAttendance.Service.wwwroot.";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
    };

    private sealed record Asset(byte[] Content, string ContentType);

    private static readonly Dictionary<string, Asset> Assets = Load();

    private static Dictionary<string, Asset> Load()
    {
        var map = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            var fileName = resource.Substring(ResourcePrefix.Length);
            var ext = Path.GetExtension(fileName);
            if (!ContentTypes.TryGetValue(ext, out var contentType)) continue;

            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            map["/" + fileName] = new Asset(ms.ToArray(), contentType);
        }
        return map;
    }

    /// <summary>Number of embedded assets — logged at startup as a sanity check.</summary>
    public static int Count => Assets.Count;

    public static void UseEmbeddedUi(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                await next();
                return;
            }

            var path = ctx.Request.Path.Value ?? "/";

            // Friendly URLs for the two pages.
            if (path == "/") path = "/index.html";
            else if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)) path = "/login.html";

            // Exact dictionary lookup only — there is no filesystem walk here,
            // so path traversal has nothing to traverse.
            if (!Assets.TryGetValue(path, out var asset))
            {
                await next();
                return;
            }

            ctx.Response.ContentType = asset.ContentType;
            ctx.Response.ContentLength = asset.Content.Length;
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await ctx.Response.Body.WriteAsync(asset.Content);
        });
    }
}
