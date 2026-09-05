using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PglAttendance.Core.Security;

/// <summary>
/// The single admin account for browser/API access, persisted to
/// credentials.json in ProgramData.
///
/// Deliberately NOT part of settings.json: the desktop app reads and rewrites
/// settings freely, whereas this file is only ever written by the service
/// process (LocalSystem), and on Windows its ACL is tightened to SYSTEM +
/// Administrators so a standard user cannot read the hash for offline cracking.
///
/// Only a PBKDF2 hash is stored — the password itself is never written to disk,
/// never logged, and never returned by any endpoint.
/// </summary>
public sealed class CredentialStore
{
    /// <summary>
    /// Minimum password length. Remote access is a single-factor,
    /// internet-adjacent surface, so this is deliberately above the usual 8.
    /// </summary>
    public const int MinPasswordLength = 12;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
    private Record _current;

    public CredentialStore() : this(Paths.CredentialsFile) { }

    public CredentialStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    private sealed class Record
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";

        /// <summary>
        /// Changes whenever the username or password changes. Sessions carry the
        /// stamp they were issued under, so a credential change instantly
        /// invalidates every existing session everywhere.
        /// </summary>
        [JsonPropertyName("securityStamp")] public string SecurityStamp { get; set; } = "";

        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";

        /// <summary>
        /// Password for an administrator-supplied .pfx. Lives here rather than
        /// in settings.json because this file is Administrators-only, while
        /// settings.json is rewritten by the desktop app as a normal user.
        /// </summary>
        [JsonPropertyName("certificatePassword")] public string CertificatePassword { get; set; } = "";
    }

    /// <summary>False until an admin has set a username and password.</summary>
    public bool IsConfigured
    {
        get { lock (_lock) return _current.Username.Length > 0 && _current.PasswordHash.Length > 0; }
    }

    public string Username
    {
        get { lock (_lock) return _current.Username; }
    }

    public string SecurityStamp
    {
        get { lock (_lock) return _current.SecurityStamp; }
    }

    public string CertificatePassword
    {
        get { lock (_lock) return _current.CertificatePassword; }
    }

    /// <summary>
    /// Stores the .pfx password in the protected file. Does not touch the
    /// account, so it neither rotates the security stamp nor logs anyone out.
    /// </summary>
    public void SetCertificatePassword(string? password)
    {
        lock (_lock)
        {
            var next = Copy(_current);
            next.CertificatePassword = password ?? "";
            Save(_path, next);
            _current = next;
        }
    }

    /// <summary>
    /// Validates a login. Always runs the full hash comparison when credentials
    /// exist so a wrong username and a wrong password cost the same time.
    /// </summary>
    public bool Validate(string username, string password)
    {
        string storedUser, storedHash;
        lock (_lock)
        {
            storedUser = _current.Username;
            storedHash = _current.PasswordHash;
        }
        if (storedUser.Length == 0 || storedHash.Length == 0) return false;

        var userOk = string.Equals(username ?? "", storedUser, StringComparison.OrdinalIgnoreCase);
        var passOk = PasswordHasher.Verify(password ?? "", storedHash);
        return userOk && passOk;
    }

    public sealed record ChangeResult(bool Ok, string? Error);

    /// <summary>
    /// Sets the username and password. When credentials already exist,
    /// <paramref name="currentPassword"/> must match — so someone who reaches
    /// the loopback API cannot silently take over an account without knowing
    /// the existing password, and a hijacked browser session cannot either.
    /// </summary>
    public ChangeResult SetCredentials(string? username, string? newPassword, string? currentPassword)
    {
        username = (username ?? "").Trim();
        newPassword ??= "";

        if (username.Length == 0) return new ChangeResult(false, "Username is required.");
        if (username.Length > 64) return new ChangeResult(false, "Username must be 64 characters or fewer.");
        if (newPassword.Length < MinPasswordLength)
            return new ChangeResult(false, $"Password must be at least {MinPasswordLength} characters.");
        if (newPassword.Length > 256) return new ChangeResult(false, "Password must be 256 characters or fewer.");
        if (newPassword.Trim().Length == 0) return new ChangeResult(false, "Password cannot be only whitespace.");

        lock (_lock)
        {
            var configured = _current.Username.Length > 0 && _current.PasswordHash.Length > 0;
            if (configured && !PasswordHasher.Verify(currentPassword ?? "", _current.PasswordHash))
                return new ChangeResult(false, "Current password is incorrect.");

            var next = new Record
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(newPassword),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                UpdatedAt = DateTime.UtcNow.ToString("o"),
                CertificatePassword = _current.CertificatePassword,
            };
            Save(_path, next);
            _current = next;
        }
        return new ChangeResult(true, null);
    }

    private static Record Copy(Record r) => new()
    {
        Username = r.Username,
        PasswordHash = r.PasswordHash,
        SecurityStamp = r.SecurityStamp,
        UpdatedAt = r.UpdatedAt,
        CertificatePassword = r.CertificatePassword,
    };

    /// <summary>
    /// Removes the stored account. Remote access cannot be enabled without
    /// credentials, so this also effectively closes the browser surface.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _current = new Record();
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        }
    }

    private static Record Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Record();
            var parsed = JsonSerializer.Deserialize<Record>(File.ReadAllText(path));
            return parsed ?? new Record();
        }
        catch
        {
            // A corrupt file must not take the service down; it reads as
            // "no credentials", which keeps remote access closed.
            return new Record();
        }
    }

    private static void Save(string path, Record rec)
    {
        Paths.EnsureDirs();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec, JsonOpts));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        FileAcl.RestrictToAdministrators(path);
    }
}
