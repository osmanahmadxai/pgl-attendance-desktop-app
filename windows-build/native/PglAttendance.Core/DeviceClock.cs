using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PglAttendance.Core;

/// <summary>
/// The clock an attendance terminal is set to. In ADMS mode the terminal has no
/// timezone of its own: it adopts whatever the server hands it (as minutes east
/// of UTC) and then shows and stamps punches in that wall clock. Zones are IANA
/// names (<c>Asia/Kabul</c>); the offset is recomputed from the zone each time
/// it is sent, so a daylight-saving zone follows its clock changes.
/// </summary>
public static class DeviceClock
{
    /// <summary>The zone used for any device nobody has configured.</summary>
    public const string DefaultZone = "Asia/Kabul";

    /// <summary>
    /// Resolves an IANA zone name. Returns false for anything this machine does
    /// not know. <paramref name="canonical"/> is the name to store and send on.
    /// </summary>
    public static bool TryResolve(string? name, out TimeZoneInfo zone, out string canonical)
    {
        zone = TimeZoneInfo.Utc;
        canonical = "";
        var id = (name ?? "").Trim();
        if (id.Length == 0 || id.Length > 64) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            canonical = id;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A Windows machine without ICU data cannot look up IANA names.
            // Kabul is the one zone the organisation must always be able to use.
            if (string.Equals(id, DefaultZone, StringComparison.OrdinalIgnoreCase))
            {
                zone = TimeZoneInfo.CreateCustomTimeZone(DefaultZone, TimeSpan.FromMinutes(270), DefaultZone, DefaultZone);
                canonical = DefaultZone;
                return true;
            }
            return false;
        }
    }

    /// <summary>Like <see cref="TryResolve"/>, falling back to <see cref="DefaultZone"/>.</summary>
    public static TimeZoneInfo ResolveOrDefault(string? name)
        => TryResolve(name, out var zone, out _) ? zone : Resolve(DefaultZone);

    private static TimeZoneInfo Resolve(string name)
    {
        TryResolve(name, out var zone, out _);
        return zone;
    }

    /// <summary>Minutes the zone's clock is ahead of UTC at <paramref name="utcNow"/> — what the terminal's TimeZone option takes.</summary>
    public static int OffsetMinutes(TimeZoneInfo zone, DateTime utcNow)
        => (int)Math.Round(zone.GetUtcOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).TotalMinutes);

    /// <summary>
    /// The zone's current wall clock in ZK's packed-integer encoding, which is
    /// what the terminal fetches after accepting "SET OPTIONS DateTime".
    /// </summary>
    public static string PackedWallClock(TimeZoneInfo zone, DateTime utcNow)
    {
        var k = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
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
        var zone = ResolveOrDefault(zoneName);
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        try
        {
            // A wall time inside a daylight-saving gap does not exist; read it
            // as the standard-time reading so ordering still works.
            var offset = zone.IsInvalidTime(unspecified)
                ? zone.BaseUtcOffset
                : zone.GetUtcOffset(unspecified);
            utc = DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>IANA names a person can choose from on this machine, sorted.</summary>
    public static IReadOnlyList<string> ListZoneNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal) { DefaultZone };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (zone.Id.Contains('/'))
            {
                names.Add(zone.Id);
            }
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && iana is not null)
            {
                names.Add(iana);
            }
        }
        return names.ToList();
    }
}
