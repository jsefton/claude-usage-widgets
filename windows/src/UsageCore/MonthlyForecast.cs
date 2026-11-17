namespace UsageCore;

/// <summary>How the current pace compares with a monthly spend limit.</summary>
public enum ForecastStatus
{
    /// <summary>Projected to finish at or under 90% of the limit.</summary>
    OnTrack,
    /// <summary>Projected to finish between 90% and 100% of the limit.</summary>
    Close,
    /// <summary>Projected to go over the limit.</summary>
    Over,
}

/// <summary>
/// A forecast for a monthly spend limit (Enterprise plans): when the month resets, how much can
/// still be spent per working day (Monday–Friday), and whether the current pace stays under the limit.
/// </summary>
/// <param name="PeriodStart">Start of the current monthly period (local midnight on the reset day).</param>
/// <param name="ResetsAt">When the monthly spend resets (local midnight on the next reset day).</param>
/// <param name="WorkingDaysLeft">Working days from today (included) up to the reset (excluded).</param>
/// <param name="WorkingDaysElapsed">Working days from the start of the period up to and including today.</param>
/// <param name="Remaining">Limit minus spend so far, never negative.</param>
/// <param name="PerWorkingDay">What can be spent per working day left without going over, or null when no working days are left.</param>
/// <param name="Projected">Projected spend at the reset if the average per working day so far continues.</param>
/// <param name="Status">How the projection compares with the limit.</param>
/// <param name="ProjectedOver">How far over the limit the projection is (0 when it isn't).</param>
public sealed record MonthlyForecast(
    DateTime PeriodStart,
    DateTime ResetsAt,
    int WorkingDaysLeft,
    int WorkingDaysElapsed,
    double Remaining,
    double? PerWorkingDay,
    double Projected,
    ForecastStatus Status,
    double ProjectedOver)
{
    /// <summary>Builds a forecast, or returns null when there's no positive limit.</summary>
    /// <param name="resetDay">Day of the month the spend resets (1–31; clamped to shorter months).</param>
    /// <param name="now">The current local time.</param>
    public static MonthlyForecast? Make(double used, double limit, int resetDay, DateTime now)
    {
        if (!(limit > 0) || !double.IsFinite(used)) return null;
        var (start, end) = Period(resetDay, now);
        var today = now.Date;

        var left = WorkingDays(today, end);
        var elapsed = WorkingDays(start, today.AddDays(1));
        var leftAfterToday = Math.Max(left - (IsWorkingDay(today) ? 1 : 0), 0);

        var averagePerDay = used / Math.Max(elapsed, 1);
        var projected = used + averagePerDay * leftAfterToday;
        var remaining = Math.Max(limit - used, 0);

        var status = projected > limit ? ForecastStatus.Over
            : projected > limit * 0.9 ? ForecastStatus.Close
            : ForecastStatus.OnTrack;

        return new MonthlyForecast(
            PeriodStart: start,
            ResetsAt: end,
            WorkingDaysLeft: left,
            WorkingDaysElapsed: elapsed,
            Remaining: remaining,
            PerWorkingDay: left > 0 ? remaining / left : null,
            Projected: projected,
            Status: status,
            ProjectedOver: Math.Max(projected - limit, 0));
    }

    /// <summary>
    /// The monthly period containing <paramref name="date"/>: from the most recent reset day (at local
    /// midnight) to the next one. A reset day past the end of a short month falls on its last day.
    /// </summary>
    public static (DateTime Start, DateTime End) Period(int resetDay, DateTime date)
    {
        var day = Math.Clamp(resetDay, 1, 31);
        var firstOfMonth = new DateTime(date.Year, date.Month, 1, 0, 0, 0, date.Kind);
        var thisMonth = ResetDate(day, firstOfMonth);
        return date >= thisMonth
            ? (thisMonth, ResetDate(day, firstOfMonth.AddMonths(1)))
            : (ResetDate(day, firstOfMonth.AddMonths(-1)), thisMonth);
    }

    /// <summary>Monday–Friday days in [<paramref name="from"/>, <paramref name="to"/>), counted by calendar day.</summary>
    public static int WorkingDays(DateTime from, DateTime to)
    {
        var count = 0;
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            if (IsWorkingDay(day)) count++;
        }
        return count;
    }

    /// <summary>Monday to Friday.</summary>
    public static bool IsWorkingDay(DateTime date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    static DateTime ResetDate(int day, DateTime firstOfMonth) =>
        firstOfMonth.AddDays(Math.Min(day, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month)) - 1);
}
