namespace UsageCore;

/// <summary>A "usage day" that starts at a configurable local hour (default 08:00) rather than midnight.</summary>
public readonly record struct UsagePeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset date) => date >= Start && date < End;

    /// <summary>
    /// The period containing <paramref name="now"/>. Before the reset hour, the period started yesterday at the reset hour.
    /// </summary>
    public static UsagePeriod Current(DateTimeOffset? now = null, int resetHour = 8, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var instant = now ?? DateTimeOffset.Now;
        var hour = Math.Clamp(resetHour, 0, 23);

        // Work in wall-clock time in the given zone, then convert back to instants.
        var today = TimeZoneInfo.ConvertTime(instant, tz).DateTime.Date;
        var todayStart = ToInstant(today.AddHours(hour), tz);
        var startDay = instant >= todayStart ? today : today.AddDays(-1);

        // Add a calendar day (not 24 hours) so DST changes keep the reset at the same wall-clock hour.
        return new UsagePeriod(ToInstant(startDay.AddHours(hour), tz), ToInstant(startDay.AddDays(1).AddHours(hour), tz));
    }

    /// <summary>Converts a wall-clock time in <paramref name="tz"/> to an instant.</summary>
    internal static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        // In a spring-forward gap the time doesn't exist: use the first time after the gap.
        for (var i = 0; i < 24 * 60 && tz.IsInvalidTime(local); i++) local = local.AddMinutes(1);

        // In a fall-back overlap the time happens twice: use the first occurrence (the larger offset).
        if (tz.IsAmbiguousTime(local))
        {
            return new DateTimeOffset(local, tz.GetAmbiguousTimeOffsets(local).Max());
        }
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }
}
