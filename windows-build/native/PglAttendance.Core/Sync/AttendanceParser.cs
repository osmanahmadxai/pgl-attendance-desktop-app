using PglAttendance.Core.Models;

namespace PglAttendance.Core.Sync;

public static class AttendanceParser
{
    public readonly record struct ParsedFields(string UserId, string DateTime, string Status, string VerifyType);

    /// <summary>
    /// Mirror of NestJS parseRawData: tab-split, first 4 fields, blank fallback.
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
