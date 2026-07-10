using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PglAttendance.Core.Sync;

/// <summary>
/// HTTP POST raw text body to {hrmisUrl}/iclock/cdata.
/// HRMIS replies with a literal "OK" body on success, HTTP 400 with an error
/// body for records it recognizes but cannot parse, and 5xx on server faults.
///
/// Failures are classified so the sync engine can act correctly:
///   - IsTransient = true  → network error / timeout / 5xx. The record is fine;
///     HRMIS is unreachable or broken. Retry the SAME record (never skip it,
///     or later records would arrive out of order).
///   - IsTransient = false → HRMIS actively rejected the record (4xx, or a 2xx
///     whose body isn't "OK"). Retrying will never succeed; record the error
///     and move on.
/// </summary>
public sealed class HrmisClient
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// HasResponse distinguishes "HRMIS answered with an error" (true — a 5xx
    /// repeated many times for the same record suggests that record poisons
    /// the server) from "HRMIS is unreachable" (false — nothing is wrong with
    /// the record; retry indefinitely).
    /// </summary>
    public sealed record Result(bool Ok, string ResponseText, string? Error, bool IsTransient, bool HasResponse);

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
            // Trim so a proxy-added trailing newline can't turn an accepted
            // record into a false rejection.
            if (resp.IsSuccessStatusCode && string.Equals(text.Trim(), "OK", StringComparison.Ordinal))
                return new Result(true, text, null, false, true);
            var code = (int)resp.StatusCode;
            // 408 (request timeout) and 429 (rate limited) are retryable even
            // though they're 4xx — treating them as permanent would drop records.
            var transient = code >= 500 || code == 408 || code == 429;
            return new Result(false, text, $"HRMIS returned: {text}", transient, true);
        }
        catch (Exception ex)
        {
            return new Result(false, "", ex.Message, true, false);
        }
    }
}
