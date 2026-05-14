using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PglAttendance.Core.Sync;

/// <summary>
/// HTTP POST raw text body to {hrmisUrl}/iclock/cdata.
/// Treat a literal "OK" response as success — same as NestJS.
/// </summary>
public sealed class HrmisClient
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public sealed record Result(bool Ok, string ResponseText, string? Error);

    public async Task<Result> PostAsync(string hrmisUrl, string rawData, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = (hrmisUrl ?? "").TrimEnd('/');
            var fullUrl = baseUrl + "/iclock/cdata";
            using var content = new StringContent(rawData, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            using var resp = await _http.PostAsync(fullUrl, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (string.Equals(text, "OK", StringComparison.Ordinal))
                return new Result(true, text, null);
            return new Result(false, text, $"HRMIS returned: {text}");
        }
        catch (Exception ex)
        {
            return new Result(false, "", ex.Message);
        }
    }
}
