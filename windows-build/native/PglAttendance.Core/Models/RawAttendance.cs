using System;
using System.Text.Json.Serialization;

namespace PglAttendance.Core.Models;

/// <summary>
/// Mirrors the Prisma RawAttendance row 1:1 (table "RawAttendance",
/// columns: id, rawData, isSynced, createdAt, retryCount, lastError).
/// </summary>
public sealed class RawAttendance
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("rawData")]
    public string RawData { get; set; } = "";

    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

/// <summary>
/// Parsed view sent over the wire to the desktop UI — matches the
/// JSON shape the NestJS controller used to return.
/// </summary>
public sealed class ParsedAttendanceVm
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("datetime")]
    public string DateTime { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("verifyType")]
    public string VerifyType { get; set; } = "";

    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}
