using System;
using System.Text.Json.Serialization;

namespace PglAttendance.Core;

public sealed class AppSettings
{
    [JsonPropertyName("hrmisUrl")]
    public string HrmisUrl { get; set; } = "https://people-api.pglsystem.com";

    /// <summary>Device port (plain HTTP). Serves /iclock/* to the network, and the full API to loopback only.</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 4001;

    /// <summary>
    /// Master switch for browser/API access from other machines. Off by
    /// default: upgrading an existing installation changes nothing until an
    /// administrator deliberately opens it.
    /// </summary>
    [JsonPropertyName("remoteAccessEnabled")]
    public bool RemoteAccessEnabled { get; set; }

    /// <summary>HTTPS port for the browser UI and API. Only bound while remote access is enabled.</summary>
    [JsonPropertyName("adminHttpsPort")]
    public int AdminHttpsPort { get; set; } = 4443;

    /// <summary>
    /// IPs/CIDRs permitted to reach the browser UI. Empty means any address may
    /// try — authentication is still required either way.
    /// </summary>
    [JsonPropertyName("allowedIps")]
    public string[] AllowedIps { get; set; } = Array.Empty<string>();

    /// <summary>
    /// IPs/CIDRs permitted to POST device data to /iclock/*. Empty (the
    /// default) means any address, which is what keeps attendance collection
    /// working unchanged after an upgrade.
    /// </summary>
    [JsonPropertyName("deviceAllowedIps")]
    public string[] DeviceAllowedIps { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional path to an administrator-supplied .pfx. Empty uses the
    /// self-signed certificate generated in ProgramData. The matching password
    /// lives in the protected credentials file, never here.
    /// </summary>
    [JsonPropertyName("certificatePath")]
    public string CertificatePath { get; set; } = "";

    public AppSettings Clone() => new()
    {
        HrmisUrl = HrmisUrl,
        Port = Port,
        RemoteAccessEnabled = RemoteAccessEnabled,
        AdminHttpsPort = AdminHttpsPort,
        AllowedIps = (string[])AllowedIps.Clone(),
        DeviceAllowedIps = (string[])DeviceAllowedIps.Clone(),
        CertificatePath = CertificatePath,
    };
}
