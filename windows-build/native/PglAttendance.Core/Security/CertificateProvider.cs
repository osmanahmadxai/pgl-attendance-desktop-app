using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PglAttendance.Core.Security;

/// <summary>
/// Supplies the TLS certificate for the browser listener.
///
/// Order of preference: an administrator-supplied .pfx (real certificate, no
/// browser warning), otherwise a self-signed certificate generated once and
/// persisted to ProgramData with an Administrators-only ACL.
///
/// The generated certificate carries the machine name, "localhost" and every
/// local IPv4 address as SANs, so browsing by IP produces only the expected
/// "not a trusted authority" warning rather than an additional name mismatch.
/// </summary>
public static class CertificateProvider
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3 * 365);

    public sealed record Result(X509Certificate2? Certificate, string? Error, bool SelfSigned);

    /// <summary>
    /// Loads or creates the certificate. Never throws — a failure is returned
    /// as an error string so the caller can log it and leave remote access off
    /// without disturbing the device listener.
    /// </summary>
    public static Result Load(string? customPfxPath, string? customPfxPassword)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(customPfxPath))
            {
                if (!File.Exists(customPfxPath))
                    return new Result(null, $"Certificate file not found: {customPfxPath}", false);

                var custom = LoadPfx(File.ReadAllBytes(customPfxPath), customPfxPassword ?? "");
                if (!custom.HasPrivateKey)
                    return new Result(null, "Certificate has no private key — a .pfx with the key is required.", false);
                return new Result(custom, null, false);
            }

            if (File.Exists(Paths.CertificateFile))
            {
                try
                {
                    var existing = LoadPfx(File.ReadAllBytes(Paths.CertificateFile), "");
                    // Regenerate well before expiry so access never lapses silently.
                    if (existing.NotAfter > DateTime.Now.AddDays(30) && existing.HasPrivateKey)
                        return new Result(existing, null, true);
                }
                catch
                {
                    // Unreadable/corrupt — fall through and generate a new one.
                }
            }

            var generated = GenerateSelfSigned();
            Paths.EnsureDirs();
            File.WriteAllBytes(Paths.CertificateFile, generated.Export(X509ContentType.Pfx));
            FileAcl.RestrictToAdministrators(Paths.CertificateFile);

            // Re-load from disk so the key lands in the machine key store the
            // same way it will on every subsequent start.
            var reloaded = LoadPfx(File.ReadAllBytes(Paths.CertificateFile), "");
            return new Result(reloaded, null, true);
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message, false);
        }
    }

    /// <summary>Colon-separated SHA-256 fingerprint, for out-of-band verification.</summary>
    public static string Fingerprint(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return BitConverter.ToString(hash).Replace('-', ':');
    }

    private static X509Certificate2 LoadPfx(byte[] bytes, string password)
    {
        // SChannel cannot use an ephemeral key set for server authentication,
        // so on Windows the key must go to the machine store. The service runs
        // as LocalSystem, which is allowed to write there.
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.Exportable;
        return new X509Certificate2(bytes, password, flags);
    }

    private static X509Certificate2 GenerateSelfSigned()
    {
        var host = SafeHostName();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}, O=PGL Attendance",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // server auth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LocalAddresses())
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
    }

    private static string SafeHostName()
    {
        try
        {
            var name = Dns.GetHostName();
            return string.IsNullOrWhiteSpace(name) ? "localhost" : name;
        }
        catch { return "localhost"; }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        var seen = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!seen.Contains(addr.Address)) seen.Add(addr.Address);
                }
            }
        }
        catch
        {
            // Enumeration can fail on locked-down hosts; the DNS name SAN still applies.
        }
        return seen;
    }
}
