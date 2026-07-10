using System;
using System.Globalization;
using System.Text.RegularExpressions;
using PglAttendance.Core.Models;

namespace PglAttendance.Core.Sync;

public static class AttendanceParser
{
    public readonly record struct ParsedFields(string UserId, string DateTime, string Status, string VerifyType);

    private const string OplogPrefix = "OPLOG";

    /// <summary>Device operation-log rows — stored locally, never synced.</summary>
    public static bool IsOplog(string rawData)
        => rawData is not null && rawData.StartsWith(OplogPrefix, StringComparison.Ordinal);

    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex TimePattern = new(@"^\d{2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    /// <summary>
    /// Mirrors what the HRMIS backend actually ACCEPTS (its parseAttendanceData):
    /// at least 5 whitespace-separated fields, numeric userId, yyyy-MM-dd date,
    /// HH:mm:ss time. Status/verifyType are NOT validated — HRMIS accepts any
    /// value there and maps unknowns to "Unknown ...". Keeping this gate exactly
    /// as permissive as the consumer means no record HRMIS would store can ever
    /// be stranded locally.
    /// </summary>
    public static bool IsValidAttendanceRecord(string rawData)
    {
        if (string.IsNullOrEmpty(rawData)) return false;
        var parts = rawData.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        foreach (var ch in parts[0])
            if (ch < '0' || ch > '9') return false;
        return DatePattern.IsMatch(parts[1]) && TimePattern.IsMatch(parts[2]);
    }

    /// <summary>
    /// Extracts the punch timestamp (device-local) from a record line.
    /// HRMIS splits on any whitespace, so we do the same: parts[1] is the date,
    /// parts[2] is the time. Invariant culture — never OS-locale dependent.
    /// </summary>
    public static bool TryGetPunchTimestamp(string rawData, out DateTime punch)
    {
        punch = default;
        if (string.IsNullOrEmpty(rawData)) return false;
        var parts = rawData.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        return DateTime.TryParseExact(
            parts[1] + " " + parts[2],
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out punch);
    }

    /// <summary>
    /// Mirror of NestJS parseRawData: tab-split, first 4 fields, blank fallback.
    /// (The device sends PIN\tdate time\tstatus\tverify — the datetime keeps its
    /// inner space, so tab-splitting yields 4 fields where HRMIS sees 5.)
    /// </summary>
    public static ParsedFields Parse(string rawData)
    {
        var parts = rawData.Split('\t');
        if (parts.Length >= 4)
            return new ParsedFields(parts[0], parts[1], parts[2], parts[3]);
        return new ParsedFields("", "", "", "");
    }

    public static ParsedAttendanceVm ToVm(RawAttendance row)
    {
        var p = Parse(row.RawData);
        return new ParsedAttendanceVm
        {
            Id = row.Id,
            UserId = p.UserId,
            DateTime = p.DateTime,
            Status = p.Status,
            VerifyType = p.VerifyType,
            IsSynced = row.IsSynced,
            CreatedAt = row.CreatedAt,
            LastError = row.LastError,
        };
    }
}
