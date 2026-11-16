import XCTest
@testable import UsageCore

final class PlanUsageTests: XCTestCase {
    private func parse(_ json: String) throws -> PlanUsage {
        try XCTUnwrap(PlanUsage.parse(Data(json.utf8)))
    }

    /// Trimmed from a real Max-plan response: usage credits capped at £40, currently off.
    func testMaxPlanWithCreditsCap() throws {
        let usage = try parse("""
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
        """)

        let monthly = try XCTUnwrap(usage.monthly)
        XCTAssertEqual(monthly.used, 0)
        XCTAssertEqual(monthly.limit, 40)
        XCTAssertEqual(monthly.currency, "GBP")
        XCTAssertEqual(monthly.percent, 0)
        XCTAssertFalse(monthly.isEnabled)
        XCTAssertEqual(monthly.disabledReason, "out_of_credits")
        XCTAssertNil(monthly.resetsAt)

        XCTAssertEqual(usage.limits.map(\.label), ["5-hour session", "Weekly", "Weekly · Fable"])
        XCTAssertEqual(usage.limits[1].percent, 55)
        XCTAssertEqual(usage.limits[0].resetsAt, PlanUsage.parseDate("2026-11-16T20:30:00.855Z"))
    }

    /// Enterprise-style: a monthly spend limit with a "monthly" meter carrying the reset date.
    func testMonthlySpendLimitWithResetDate() throws {
        let usage = try parse("""
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
        """)
        let monthly = try XCTUnwrap(usage.monthly)
        XCTAssertEqual(monthly.used, 127.50, accuracy: 0.001)
        XCTAssertEqual(monthly.limit, 500)
        XCTAssertEqual(monthly.percent, 25.5)
        XCTAssertTrue(monthly.isEnabled)
        XCTAssertEqual(monthly.resetsAt, PlanUsage.parseDate("2026-12-01T00:00:00Z"))
        XCTAssertEqual(usage.limits.first?.label, "Monthly")
    }

    /// Older shape without `spend`: falls back to `extra_usage`, amounts in cents.
    func testExtraUsageFallback() throws {
        let usage = try parse("""
        {"extra_usage": {"is_enabled": true, "monthly_limit": 50000, "used_credits": 1234,
                         "utilization": 2.468, "currency": "usd"}}
        """)
        let monthly = try XCTUnwrap(usage.monthly)
        XCTAssertEqual(monthly.used, 12.34, accuracy: 0.001)
        XCTAssertEqual(monthly.limit, 500)
        XCTAssertEqual(monthly.currency, "USD")
        XCTAssertTrue(usage.limits.isEmpty)
    }

    /// Spend with no cap: shows the amount only, no percentage.
    func testSpendWithoutLimit() throws {
        let usage = try parse("""
        {"spend": {"used": {"amount_minor": 999, "currency": "USD", "exponent": 2}, "limit": null}}
        """)
        let monthly = try XCTUnwrap(usage.monthly)
        XCTAssertEqual(monthly.used, 9.99, accuracy: 0.001)
        XCTAssertNil(monthly.limit)
        XCTAssertNil(monthly.percent)
    }

    func testNoPlanData() throws {
        let usage = try parse(#"{"five_hour": null, "limits": []}"#)
        XCTAssertNil(usage.monthly)
        XCTAssertTrue(usage.limits.isEmpty)
        XCTAssertNil(PlanUsage.parse(Data("not json".utf8)))
    }

    func testDateParsingVariants() {
        XCTAssertNotNil(PlanUsage.parseDate("2026-11-18T01:59:59.855155+00:00"))
        XCTAssertNotNil(PlanUsage.parseDate("2026-11-18T02:00:00+00:00"))
        XCTAssertNotNil(PlanUsage.parseDate("2026-11-18T02:00:00Z"))
        XCTAssertNil(PlanUsage.parseDate(""))
        XCTAssertNil(PlanUsage.parseDate(nil))
    }
}
