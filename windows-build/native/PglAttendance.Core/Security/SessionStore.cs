using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PglAttendance.Core.Security;

/// <summary>
/// In-memory browser sessions.
///
/// Deliberately not persisted: a service restart logging everyone out is the
/// safe failure mode, and it means no session material is ever written to disk.
///
/// Every session records the credential <c>SecurityStamp</c> it was issued
/// under, so changing the password or username instantly invalidates all
/// existing sessions — including any an attacker might hold.
/// </summary>
public sealed class SessionStore
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);

    /// <summary>Stops unbounded growth if something ever hammers the login endpoint.</summary>
    private const int MaxSessions = 500;

    private sealed record Session(string Username, string SecurityStamp, DateTime CreatedUtc, DateTime LastSeenUtc);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>Creates a session and returns its opaque 256-bit token.</summary>
    public string Create(string username, string securityStamp)
    {
        if (_sessions.Count >= MaxSessions) Prune();

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        _sessions[token] = new Session(username, securityStamp, now, now);
        return token;
    }

    /// <summary>
    /// Returns the username for a live session and slides its idle window, or
    /// null when the token is unknown, expired, or was issued under superseded
    /// credentials.
    /// </summary>
    public string? Validate(string? token, string currentSecurityStamp)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var s)) return null;

        var now = DateTime.UtcNow;
        if (now - s.CreatedUtc > AbsoluteTimeout || now - s.LastSeenUtc > IdleTimeout)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        if (!string.Equals(s.SecurityStamp, currentSecurityStamp, StringComparison.Ordinal))
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        _sessions[token] = s with { LastSeenUtc = now };
        return s.Username;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    /// <summary>Drops every session — used when credentials change.</summary>
    public void RevokeAll() => _sessions.Clear();

    public int Count => _sessions.Count;

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (now - s.CreatedUtc > AbsoluteTimeout || now - s.LastSeenUtc > IdleTimeout)
                _sessions.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>URL/cookie-safe base64 (no '+', '/' or '=' to be mangled in transit).</summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
