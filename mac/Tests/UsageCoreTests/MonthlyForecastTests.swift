import XCTest
@testable import UsageCore

final class MonthlyForecastTests: XCTestCase {
    private var calendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "Europe/London")!
        c.locale = Locale(identifier: "en_GB")
        return c
    }()

    private func date(_ y: Int, _ m: Int, _ d: Int, _ h: Int = 12) -> Date {
        calendar.date(from: DateComponents(year: y, month: m, day: d, hour: h))!
    }

    func testPeriodAroundResetDay() {
        // Reset on the 1st: Tue 17 Nov 2026 is in 1 Nov – 1 Dec.
        let p = MonthlyForecast.period(resetDay: 1, containing: date(2026, 11, 17), calendar: calendar)
        XCTAssertEqual(p.start, date(2026, 11, 1, 0))
        XCTAssertEqual(p.end, date(2026, 12, 1, 0))

        // Reset on the 20th, before it: 20 Oct – 20 Nov.
        let q = MonthlyForecast.period(resetDay: 20, containing: date(2026, 11, 17), calendar: calendar)
        XCTAssertEqual(q.start, date(2026, 10, 20, 0))
        XCTAssertEqual(q.end, date(2026, 11, 20, 0))

        // Exactly at the reset starts the new period.
        let r = MonthlyForecast.period(resetDay: 20, containing: date(2026, 11, 20, 0), calendar: calendar)
        XCTAssertEqual(r.start, date(2026, 11, 20, 0))
        XCTAssertEqual(r.end, date(2026, 12, 20, 0))
    }

    func testResetDayClampsToShortMonths() {
        // The 31st falls on 30 Nov and 28 Feb.
        let p = MonthlyForecast.period(resetDay: 31, containing: date(2026, 11, 17), calendar: calendar)
        XCTAssertEqual(p.start, date(2026, 10, 31, 0))
        XCTAssertEqual(p.end, date(2026, 11, 30, 0))

        let q = MonthlyForecast.period(resetDay: 31, containing: date(2027, 2, 10), calendar: calendar)
        XCTAssertEqual(q.start, date(2027, 1, 31, 0))
        XCTAssertEqual(q.end, date(2027, 2, 28, 0))
    }

    func testWorkingDaysSkipWeekends() {
        // Mon 16 Nov to Mon 23 Nov: Mon–Fri only.
        XCTAssertEqual(MonthlyForecast.workingDays(from: date(2026, 11, 16, 0), to: date(2026, 11, 23, 0), calendar: calendar), 5)
        // Sat to Mon: none.
        XCTAssertEqual(MonthlyForecast.workingDays(from: date(2026, 11, 21, 0), to: date(2026, 11, 23, 0), calendar: calendar), 0)
    }

    /// The Enterprise screenshot: US$15.11 of US$500 on Tue 17 Nov, resetting on the 1st.
    func testOnTrack() throws {
        let f = try XCTUnwrap(MonthlyForecast.make(used: 15.11, limit: 500, resetDay: 1, now: date(2026, 11, 17), calendar: calendar))
        XCTAssertEqual(f.resetsAt, date(2026, 12, 1, 0))
        // 1–17 Nov: 12 working days so far (incl. today). 17–30 Nov: 10 working days left (incl. today).
        XCTAssertEqual(f.workingDaysElapsed, 12)
        XCTAssertEqual(f.workingDaysLeft, 10)
        XCTAssertEqual(try XCTUnwrap(f.perWorkingDay), (500 - 15.11) / 10, accuracy: 0.001)
        // 15.11 + 15.11/12 per day for the 9 working days after today.
        XCTAssertEqual(f.projected, 15.11 + 15.11 / 12 * 9, accuracy: 0.001)
        XCTAssertEqual(f.status, .onTrack)
        XCTAssertEqual(f.projectedOver, 0)
    }

    func testCloseAndOver() throws {
        // 12 working days at 22/day = 264; 9 more days → 462 of 500 (92%).
        let close = try XCTUnwrap(MonthlyForecast.make(used: 264, limit: 500, resetDay: 1, now: date(2026, 11, 17), calendar: calendar))
        XCTAssertEqual(close.status, .close)

        // 12 working days at 30/day = 360; 9 more → 630, 130 over.
        let over = try XCTUnwrap(MonthlyForecast.make(used: 360, limit: 500, resetDay: 1, now: date(2026, 11, 17), calendar: calendar))
        XCTAssertEqual(over.status, .over)
        XCTAssertEqual(over.projectedOver, 130, accuracy: 0.001)
    }

    func testAlreadyOverLimit() throws {
        let f = try XCTUnwrap(MonthlyForecast.make(used: 520, limit: 500, resetDay: 1, now: date(2026, 11, 17), calendar: calendar))
        XCTAssertEqual(f.remaining, 0)
        XCTAssertEqual(f.perWorkingDay, 0)
        XCTAssertEqual(f.status, .over)
    }

    func testWeekendBeforeResetHasNoWorkingDaysLeft() throws {
        // Sat 28 Nov, resetting Tue 1 Dec... Mon 30 Nov is still a working day.
        let sat = try XCTUnwrap(MonthlyForecast.make(used: 100, limit: 500, resetDay: 1, now: date(2026, 11, 28), calendar: calendar))
        XCTAssertEqual(sat.workingDaysLeft, 1)
        XCTAssertEqual(try XCTUnwrap(sat.perWorkingDay), 400, accuracy: 0.001)

        // Resetting on the 30th (a Monday): from Sat 28 Nov, no working days left.
        let none = try XCTUnwrap(MonthlyForecast.make(used: 100, limit: 500, resetDay: 30, now: date(2026, 11, 28), calendar: calendar))
        XCTAssertEqual(none.workingDaysLeft, 0)
        XCTAssertNil(none.perWorkingDay)
        XCTAssertEqual(none.projected, 100, accuracy: 0.001)
    }

    func testNoForecastWithoutLimit() {
        XCTAssertNil(MonthlyForecast.make(used: 10, limit: 0, resetDay: 1, now: date(2026, 11, 17), calendar: calendar))
    }

    func testOnlyForOrganisationSpendLimits() {
        let enterprise = PlanUsage(
            monthly: MonthlySpend(used: 15.11, limit: 500, currency: "USD", percent: 3),
            limits: [], subscriptionType: "enterprise")
        XCTAssertTrue(enterprise.hasMonthlySpendLimit)
        XCTAssertNotNil(enterprise.monthlyForecast(resetDay: 1, now: date(2026, 11, 17), calendar: calendar))

        // Unknown plan type with a limit still counts.
        var unknown = enterprise
        unknown.subscriptionType = nil
        XCTAssertTrue(unknown.hasMonthlySpendLimit)

        // Max with a usage-credits cap: no forecast.
        var max = enterprise
        max.subscriptionType = "max"
        XCTAssertFalse(max.hasMonthlySpendLimit)
        XCTAssertNil(max.monthlyForecast(resetDay: 1, now: date(2026, 11, 17), calendar: calendar))

        // No limit, or spend switched off: no forecast.
        var noLimit = enterprise
        noLimit.monthly?.limit = nil
        XCTAssertFalse(noLimit.hasMonthlySpendLimit)
        var off = enterprise
        off.monthly?.isEnabled = false
        XCTAssertFalse(off.hasMonthlySpendLimit)
    }
}
