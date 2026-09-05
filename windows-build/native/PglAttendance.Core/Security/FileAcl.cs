using System;
using System.Diagnostics;

namespace PglAttendance.Core.Security;

/// <summary>
/// Locks a file down to LocalSystem + Administrators on Windows.
///
/// %PROGRAMDATA% is readable by every local user by default, so the credential
/// hash and the TLS private key would otherwise be exposed to any standard
/// account on the machine. Applied via icacls rather than the ACL APIs to keep
/// PglAttendance.Core free of Windows-only package references (the project
/// targets plain net8.0 and the build runs with -warnaserror).
///
/// Best effort by design: a failure here must never prevent the service from
/// starting or a password from being changed.
/// </summary>
public static class FileAcl
{
    // Well-known SIDs, so this works regardless of OS display language.
    private const string LocalSystemSid = "*S-1-5-18";
    private const string AdministratorsSid = "*S-1-5-32-544";

    public static void RestrictToAdministrators(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{LocalSystemSid}:(F)");
            psi.ArgumentList.Add($"{AdministratorsSid}:(F)");

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // Non-fatal: the file is still written, just with inherited ACLs.
        }
    }
}
