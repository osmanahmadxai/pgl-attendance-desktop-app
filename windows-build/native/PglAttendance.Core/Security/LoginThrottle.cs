using System;
using System.Collections.Concurrent;

namespace PglAttendance.Core.Security;

/// <summary>
/// Brute-force protection for the login endpoint.
///
/// Failures are counted independently per client IP and per username, so an
/// attacker spraying one password across many source addresses is stopped by
/// the account counter, and one spraying many passwords from one address is
/// stopped by the IP counter.
///
/// Lockout doubles with each consecutive lockout (15 min, 30, 60 …) up to a
/// cap, which turns an online guessing attack into an unusable one while a
/// legitimate admin who mistypes a few times waits minutes, not hours.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromHours(4);

    /// <summary>Failures older than this stop counting toward a lockout.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Failures;
        public int Lockouts;
        public DateTime FirstFailureUtc;
        public DateTime LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remaining lockout for a key, or null when it may attempt a login.</summary>
    public TimeSpan? RetryAfter(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (!_entries.TryGetValue(key, out var e)) return null;
        lock (e)
        {
            var remaining = e.LockedUntilUtc - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <summary>True when any of the supplied keys is currently locked out.</summary>
    public TimeSpan? RetryAfterAny(params string[] keys)
    {
        TimeSpan? longest = null;
        foreach (var k in keys)
        {
            var r = RetryAfter(k);
            if (r is not null && (longest is null || r > longest)) longest = r;
        }
        return longest;
    }

    public void RecordFailure(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            var now = DateTime.UtcNow;
            if (e.Failures == 0 || now - e.FirstFailureUtc > FailureWindow)
            {
                e.Failures = 0;
                e.FirstFailureUtc = now;
            }
            e.Failures++;
            if (e.Failures >= MaxFailures)
            {
                var ticks = BaseLockout.Ticks * (long)Math.Pow(2, Math.Min(e.Lockouts, 6));
                var lockout = TimeSpan.FromTicks(Math.Min(ticks, MaxLockout.Ticks));
                e.LockedUntilUtc = now + lockout;
                e.Lockouts++;
                e.Failures = 0;
            }
        }
    }

    /// <summary>Clears the counters for a key after a successful login.</summary>
    public void RecordSuccess(string key)
    {
        if (!string.IsNullOrEmpty(key)) _entries.TryRemove(key, out _);
    }

    public void Reset() => _entries.Clear();
}
