using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NodaTime;
using NodaTime.TimeZones;

namespace PglAttendance.Core;

/// <summary>
/// The clock an attendance terminal is set to. In ADMS mode the terminal has no
/// timezone of its own: it adopts whatever the server hands it (as minutes east
/// of UTC) and then shows and stamps punches in that wall clock. Zones are IANA
/// names (<c>Asia/Kabul</c>); the offset is recomputed from the zone each time
/// it is sent, so a daylight-saving zone follows its clock changes.
///
/// Zone rules come from the IANA database bundled with NodaTime, never from the
/// operating system: the service runs without ICU (InvariantGlobalization), and
/// Windows cannot look up IANA names without it.
/// </summary>
public static class DeviceClock
{
    /// <summary>The zone used for any device nobody has configured.</summary>
    public const string DefaultZone = "Asia/Kabul";

    private static readonly IDateTimeZoneProvider Zones = DateTimeZoneProviders.Tzdb;

    /// <summary>
    /// Resolves an IANA zone name (case-insensitive). Returns false for anything
    /// not in the IANA database. <paramref name="canonical"/> is the name to
    /// store and send on.
    /// </summary>
    public static bool TryResolve(string? name, out DateTimeZone zone, out string canonical)
    {
        zone = DateTimeZone.Utc;
        canonical = "";
        var id = (name ?? "").Trim();
        if (id.Length == 0 || id.Length > 64) return false;

        var found = Zones.GetZoneOrNull(id);
        if (found is null)
        {
            var match = Zones.Ids.FirstOrDefault(z => string.Equals(z, id, StringComparison.OrdinalIgnoreCase));
            if (match is not null) found = Zones.GetZoneOrNull(match);
        }
        if (found is null) return false;

        zone = found;
        canonical = found.Id;
        return true;
    }

    /// <summary>Like <see cref="TryResolve"/>, falling back to <see cref="DefaultZone"/>.</summary>
    public static DateTimeZone ResolveOrDefault(string? name)
        => TryResolve(name, out var zone, out _) ? zone : Zones[DefaultZone];

    /// <summary>Minutes the zone's clock is ahead of UTC at <paramref name="utcNow"/> — what the terminal's TimeZone option takes.</summary>
    public static int OffsetMinutes(DateTimeZone zone, DateTime utcNow)
        => (int)Math.Round(zone.GetUtcOffset(Instant.FromDateTimeUtc(AsUtc(utcNow))).ToTimeSpan().TotalMinutes);

    /// <summary>
    /// The zone's current wall clock in ZK's packed-integer encoding, which is
    /// what the terminal fetches after accepting "SET OPTIONS DateTime".
    /// </summary>
    public static string PackedWallClock(DateTimeZone zone, DateTime utcNow)
    {
        var k = Instant.FromDateTimeUtc(AsUtc(utcNow)).InZone(zone).LocalDateTime;
        var packed =
            (((k.Year - 2000) * 12 * 31) + ((k.Month - 1) * 31) + (k.Day - 1)) * 86400L
            + (k.Hour * 3600) + (k.Minute * 60) + k.Second;
        return packed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The real instant of a punch stamped on a terminal wall clock. Used only to
    /// put records from terminals in different zones into true chronological
    /// order — the record itself is never rewritten.
    /// </summary>
    public static bool TryPunchUtc(DateTime wallClock, string? zoneName, out DateTime utc)
    {
        utc = default;
        try
        {
            var zone = ResolveOrDefault(zoneName);
            var local = LocalDateTime.FromDateTime(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified));
            // Lenient: a wall time inside a daylight-saving gap or repeated by a
            // fall-back still lands on a sensible instant, so ordering never fails.
            utc = zone.ResolveLocal(local, Resolvers.LenientResolver).ToDateTimeUtc();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>IANA names a person can choose from, sorted. Same list on every machine.</summary>
    public static IReadOnlyList<string> ListZoneNames()
    {
        var names = new SortedSet<string>(Zones.Ids, StringComparer.Ordinal) { DefaultZone };
        return names.ToList();
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
