using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PglAttendance.Core;

namespace PglAttendance.Service.Security;

/// <summary>
/// Keeps the Windows Firewall rules in step with the configured ports.
///
/// The installer can only open a rule for the port it knew about at install
/// time (4001), so changing the port in Settings used to silently cut LAN
/// access. The service runs as LocalSystem, so it can reconcile the rules
/// itself on startup and whenever the ports change.
///
/// Two rules are managed:
///   • device — always open, so the attendance device can always deliver
///   • admin  — opened only while remote browser access is enabled, so turning
///     remote access off closes the port at the firewall as well as the socket
///
/// Every operation is best effort: a firewall failure is logged but never
/// allowed to stop the service, because the device listener matters more than
/// the rule bookkeeping.
/// </summary>
public static class FirewallManager
{
    public const string DeviceRule = "PGL Attendance (device)";
    public const string AdminRule = "PGL Attendance (admin)";

    /// <summary>The rule the installer used to create, superseded by the two above.</summary>
    private const string LegacyRule = "PGL Attendance";

    public static void Reconcile(AppSettings settings, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogDebug("Firewall reconciliation skipped — not Windows.");
            return;
        }

        try
        {
            DeleteRule(LegacyRule);

            DeleteRule(DeviceRule);
            AddRule(DeviceRule, settings.Port);
            logger.LogInformation("Firewall: device port {Port} open.", settings.Port);

            DeleteRule(AdminRule);
            if (settings.RemoteAccessEnabled)
            {
                AddRule(AdminRule, settings.AdminHttpsPort);
                logger.LogInformation("Firewall: admin HTTPS port {Port} open.", settings.AdminHttpsPort);
            }
            else
            {
                logger.LogInformation("Firewall: admin port closed (remote access disabled).");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reconcile firewall rules — LAN access may need a manual rule.");
        }
    }

    /// <summary>Removes both managed rules, e.g. on uninstall.</summary>
    public static void RemoveAll()
    {
        if (!OperatingSystem.IsWindows()) return;
        DeleteRule(DeviceRule);
        DeleteRule(AdminRule);
        DeleteRule(LegacyRule);
    }

    private static void AddRule(string name, int port)
        => Netsh($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}");

    private static void DeleteRule(string name)
        => Netsh($"advfirewall firewall delete rule name=\"{name}\"");

    private static void Netsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch
        {
            // Swallowed deliberately — see the class summary.
        }
    }
}
