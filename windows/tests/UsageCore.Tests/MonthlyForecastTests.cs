using UsageCore;

namespace UsageCore.Tests;

// Ported from mac/Tests/UsageCoreTests/MonthlyForecastTests.swift.

public class MonthlyForecastTests
{
    static DateTime Date(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, DateTimeKind.Local);

    [Fact]
    public void PeriodAroundResetDay()
    {
        // Reset on the 1st: Tue 17 Nov 2026 is in 1 Nov – 1 Dec.
        Assert.Equal((Date(2026, 11, 1, 0), Date(2026, 12, 1, 0)), MonthlyForecast.Period(1, Date(2026, 11, 17)));
        // Reset on the 20th, before it: 20 Oct – 20 Nov.
        Assert.Equal((Date(2026, 10, 20, 0), Date(2026, 11, 20, 0)), MonthlyForecast.Period(20, Date(2026, 11, 17)));
        // Exactly at the reset starts the new period.
        Assert.Equal((Date(2026, 11, 20, 0), Date(2026, 12, 20, 0)), MonthlyForecast.Period(20, Date(2026, 11, 20, 0)));
    }

    [Fact]
    public void ResetDayClampsToShortMonths()
    {
        // The 31st falls on 30 Nov and 28 Feb.
        Assert.Equal((Date(2026, 10, 31, 0), Date(2026, 11, 30, 0)), MonthlyForecast.Period(31, Date(2026, 11, 17)));
        Assert.Equal((Date(2027, 1, 31, 0), Date(2027, 2, 28, 0)), MonthlyForecast.Period(31, Date(2027, 2, 10)));
    }

    [Fact]
    public void WorkingDaysSkipWeekends()
    {
        // Mon 16 Nov to Mon 23 Nov: Mon–Fri only.
        Assert.Equal(5, MonthlyForecast.WorkingDays(Date(2026, 11, 16, 0), Date(2026, 11, 23, 0)));
        // Sat to Mon: none.
        Assert.Equal(0, MonthlyForecast.WorkingDays(Date(2026, 11, 21, 0), Date(2026, 11, 23, 0)));
    }

    /// <summary>The Enterprise screenshot: US$15.11 of US$500 on Tue 17 Nov, resetting on the 1st.</summary>
    [Fact]
    public void OnTrack()
    {
        var f = MonthlyForecast.Make(15.11, 500, 1, Date(2026, 11, 17));
        Assert.NotNull(f);
        Assert.Equal(Date(2026, 12, 1, 0), f.ResetsAt);
        // 1–17 Nov: 12 working days so far (incl. today). 17–30 Nov: 10 working days left (incl. today).
        Assert.Equal(12, f.WorkingDaysElapsed);
        Assert.Equal(10, f.WorkingDaysLeft);
        Assert.Equal((500 - 15.11) / 10, f.PerWorkingDay!.Value, 3);
        // 15.11 + 15.11/12 per day for the 9 working days after today.
        Assert.Equal(15.11 + 15.11 / 12 * 9, f.Projected, 3);
        Assert.Equal(ForecastStatus.OnTrack, f.Status);
        Assert.Equal(0, f.ProjectedOver);
    }

    [Fact]
    public void CloseAndOver()
    {
        // 12 working days at 22/day = 264; 9 more days → 462 of 500 (92%).
        Assert.Equal(ForecastStatus.Close, MonthlyForecast.Make(264, 500, 1, Date(2026, 11, 17))!.Status);

        // 12 working days at 30/day = 360; 9 more → 630, 130 over.
        var over = MonthlyForecast.Make(360, 500, 1, Date(2026, 11, 17))!;
        Assert.Equal(ForecastStatus.Over, over.Status);
        Assert.Equal(130, over.ProjectedOver, 3);
    }

    [Fact]
    public void AlreadyOverLimit()
    {
        var f = MonthlyForecast.Make(520, 500, 1, Date(2026, 11, 17))!;
        Assert.Equal(0, f.Remaining);
        Assert.Equal(0, f.PerWorkingDay);
        Assert.Equal(ForecastStatus.Over, f.Status);
    }

    [Fact]
    public void WeekendBeforeReset()
    {
        // Sat 28 Nov, resetting Tue 1 Dec: Mon 30 Nov is still a working day.
        var sat = MonthlyForecast.Make(100, 500, 1, Date(2026, 11, 28))!;
        Assert.Equal(1, sat.WorkingDaysLeft);
        Assert.Equal(400, sat.PerWorkingDay!.Value, 3);

        // Resetting on the 30th (a Monday): from Sat 28 Nov, no working days left.
        var none = MonthlyForecast.Make(100, 500, 30, Date(2026, 11, 28))!;
        Assert.Equal(0, none.WorkingDaysLeft);
        Assert.Null(none.PerWorkingDay);
        Assert.Equal(100, none.Projected, 3);
    }

    [Fact]
    public void NoForecastWithoutLimit() => Assert.Null(MonthlyForecast.Make(10, 0, 1, Date(2026, 11, 17)));

    [Fact]
    public void OnlyForOrganisationSpendLimits()
    {
        var enterprise = new PlanUsage(new MonthlySpend(15.11, 500, "USD", 2, 3), []) { SubscriptionType = "enterprise" };
        Assert.True(enterprise.HasMonthlySpendLimit);
        Assert.NotNull(enterprise.Forecast(1, Date(2026, 11, 17)));

        // Unknown plan type with a limit still counts.
        Assert.True((enterprise with { SubscriptionType = null }).HasMonthlySpendLimit);

        // Max with a usage-credits cap: no forecast.
        var max = enterprise with { SubscriptionType = "max" };
        Assert.False(max.HasMonthlySpendLimit);
        Assert.Null(max.Forecast(1, Date(2026, 11, 17)));

        // No limit, or spend switched off: no forecast.
        Assert.False((enterprise with { Monthly = enterprise.Monthly! with { Limit = null } }).HasMonthlySpendLimit);
        Assert.False((enterprise with { Monthly = enterprise.Monthly! with { IsEnabled = false } }).HasMonthlySpendLimit);
    }
}
