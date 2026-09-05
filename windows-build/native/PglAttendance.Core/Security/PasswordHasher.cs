using System;
using System.Security.Cryptography;

namespace PglAttendance.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
///
/// Format: pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;
///
/// The iteration count is stored per-hash so it can be raised later without
/// invalidating existing passwords: a hash verified with an older count is
/// re-hashed by the caller (see <see cref="NeedsUpgrade"/>).
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's 2023 floor for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Constant-time verification. Returns false for malformed or empty stored
    /// hashes rather than throwing — a corrupt credentials file must read as
    /// "wrong password", never as an unhandled fault in the request pipeline.
    /// </summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || password is null) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when a stored hash uses fewer iterations than we now require.</summary>
    public static bool NeedsUpgrade(string? stored, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        return int.TryParse(parts[1], out var used) && used < iterations;
    }
}
