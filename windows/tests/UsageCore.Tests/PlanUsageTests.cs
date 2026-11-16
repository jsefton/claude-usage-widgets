using UsageCore;

namespace UsageCore.Tests;

// Ported from mac/Tests/UsageCoreTests/PlanUsageTests.swift.

public class PlanUsageTests
{
    static PlanUsage Parse(string json)
    {
        var usage = PlanUsage.Parse(json);
        Assert.NotNull(usage);
        return usage;
    }

    /// <summary>Trimmed from a real Max-plan response: usage credits capped at £40, currently off.</summary>
    [Fact]
    public void MaxPlanWithCreditsCap()
    {
        var usage = Parse("""
        {
          "five_hour": {"utilization": 2.0, "resets_at": "2026-11-16T20:30:00.855131+00:00"},
          "extra_usage": {"is_enabled": false, "monthly_limit": 4000, "used_credits": 0.0,
                          "utilization": 0.0, "currency": "GBP", "decimal_places": 2,
                          "disabled_reason": "out_of_credits"},
          "limits": [
            {"kind": "session", "group": "session", "percent": 2, "severity": "normal",
             "resets_at": "2026-11-16T20:30:00.855131+00:00", "scope": null, "is_active": false},
            {"kind": "weekly_all", "group": "weekly", "percent": 55, "severity": "normal",
             "resets_at": "2026-11-18T01:59:59.855155+00:00", "scope": null, "is_active": true},
            {"kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal",
             "resets_at": "2026-11-18T02:00:00+00:00",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false}
          ],
          "spend": {
            "used": {"amount_minor": 0, "currency": "GBP", "exponent": 2},
            "limit": {"amount_minor": 4000, "currency": "GBP", "exponent": 2},
            "percent": 0, "severity": "normal", "enabled": false, "disabled_reason": "out_of_credits"
          },
          "some_future_field": {"anything": [1, 2, 3]}
        }
        """);

        var monthly = usage.Monthly;
        Assert.NotNull(monthly);
        Assert.Equal(0, monthly.Used);
        Assert.Equal(40, monthly.Limit);
        Assert.Equal("GBP", monthly.Currency);
        Assert.Equal(0, monthly.Percent);
        Assert.False(monthly.IsEnabled);
        Assert.Equal("out_of_credits", monthly.DisabledReason);
        Assert.Null(monthly.ResetsAt);

        Assert.Equal(new[] { "5-hour session", "Weekly", "Weekly · Fable" }, usage.Limits.Select(l => l.Label));
        Assert.Equal(55, usage.Limits[1].Percent);
        Assert.Equal(PlanUsage.ParseDate("2026-11-16T20:30:00.855131Z"), usage.Limits[0].ResetsAt);
    }

    /// <summary>Enterprise-style: a monthly spend limit with a "monthly" meter carrying the reset date.</summary>
    [Fact]
    public void MonthlySpendLimitWithResetDate()
    {
        var usage = Parse("""
        {
          "limits": [
            {"kind": "monthly_spend", "group": "monthly", "percent": 25.5, "severity": "normal",
             "resets_at": "2026-12-01T00:00:00+00:00"}
          ],
          "spend": {
            "used": {"amount_minor": 12750, "currency": "USD", "exponent": 2},
            "limit": {"amount_minor": 50000, "currency": "USD", "exponent": 2},
            "percent": 25.5, "severity": "normal", "enabled": true
          }
        }
        """);
        var monthly = usage.Monthly;
        Assert.NotNull(monthly);
        Assert.Equal(127.50, monthly.Used, 3);
        Assert.Equal(500, monthly.Limit);
        Assert.Equal(25.5, monthly.Percent);
        Assert.True(monthly.IsEnabled);
        Assert.Equal(PlanUsage.ParseDate("2026-12-01T00:00:00Z"), monthly.ResetsAt);
        Assert.Equal("Monthly", usage.Limits[0].Label);
    }

    /// <summary>Older shape without <c>spend</c>: falls back to <c>extra_usage</c>, amounts in cents.</summary>
    [Fact]
    public void ExtraUsageFallback()
    {
        var usage = Parse("""
        {"extra_usage": {"is_enabled": true, "monthly_limit": 50000, "used_credits": 1234,
                         "utilization": 2.468, "currency": "usd"}}
        """);
        var monthly = usage.Monthly;
        Assert.NotNull(monthly);
        Assert.Equal(12.34, monthly.Used, 3);
        Assert.Equal(500, monthly.Limit);
        Assert.Equal("USD", monthly.Currency);
        Assert.Empty(usage.Limits);
    }

    /// <summary>Spend with no cap: shows the amount only, no percentage.</summary>
    [Fact]
    public void SpendWithoutLimit()
    {
        var usage = Parse("""
        {"spend": {"used": {"amount_minor": 999, "currency": "USD", "exponent": 2}, "limit": null}}
        """);
        var monthly = usage.Monthly;
        Assert.NotNull(monthly);
        Assert.Equal(9.99, monthly.Used, 3);
        Assert.Null(monthly.Limit);
        Assert.Null(monthly.Percent);
    }

    [Fact]
    public void NoPlanData()
    {
        var usage = Parse("""{"five_hour": null, "limits": []}""");
        Assert.Null(usage.Monthly);
        Assert.Empty(usage.Limits);
        Assert.Null(PlanUsage.Parse("not json"));
        Assert.Null(PlanUsage.Parse("[1, 2]"));
    }

    [Fact]
    public void UnexpectedTypesAreSkippedNotFatal()
    {
        var usage = Parse("""
        {"limits": [{"kind": "session", "percent": "lots"}, {"kind": "weekly_all", "group": "weekly", "percent": 10, "resets_at": 5}],
         "spend": {"used": {"amount_minor": 100, "currency": "USD"}, "limit": "none", "percent": null}}
        """);
        Assert.Equal(new[] { "Weekly" }, usage.Limits.Select(l => l.Label));
        Assert.Null(usage.Limits[0].ResetsAt);
        Assert.Equal(1, usage.Monthly?.Used);
        Assert.Null(usage.Monthly?.Limit);
    }

    [Fact]
    public void DateParsingVariants()
    {
        Assert.NotNull(PlanUsage.ParseDate("2026-11-18T01:59:59.855155+00:00"));
        Assert.NotNull(PlanUsage.ParseDate("2026-11-18T02:00:00+00:00"));
        Assert.NotNull(PlanUsage.ParseDate("2026-11-18T02:00:00Z"));
        Assert.Null(PlanUsage.ParseDate(""));
        Assert.Null(PlanUsage.ParseDate(null));
    }
}
