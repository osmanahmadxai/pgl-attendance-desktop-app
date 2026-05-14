using System;
using System.Text.Json.Serialization;

namespace PglAttendance.Core;

public sealed class AppSettings
{
    [JsonPropertyName("hrmisUrl")]
    public string HrmisUrl { get; set; } = "https://people-api.pglsystem.com";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 4001;

    public AppSettings Clone() => new() { HrmisUrl = HrmisUrl, Port = Port };
}
