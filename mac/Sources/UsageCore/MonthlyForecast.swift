import Foundation

/// A forecast for a monthly spend limit (Enterprise plans): when the month resets, how much can
/// still be spent per working day (Monday–Friday), and whether the current pace stays under the limit.
public struct MonthlyForecast: Equatable, Sendable {
    public enum Status: String, Sendable {
        /// Projected to finish at or under 90% of the limit.
        case onTrack
        /// Projected to finish between 90% and 100% of the limit.
        case close
        /// Projected to go over the limit.
        case over
    }

    /// Start of the current monthly period (local midnight on the reset day).
    public var periodStart: Date
    /// When the monthly spend resets (local midnight on the next reset day).
    public var resetsAt: Date
    /// Working days from today (included) up to the reset (excluded).
    public var workingDaysLeft: Int
    /// Working days from the start of the period up to and including today.
    public var workingDaysElapsed: Int
    /// Limit minus spend so far, never negative.
    public var remaining: Double
    /// What can be spent per working day left without going over, or nil when no working days are left.
    public var perWorkingDay: Double?
    /// Projected spend at the reset if the average per working day so far continues.
    public var projected: Double
    public var status: Status
    /// How far over the limit the projection is (0 when it isn't).
    public var projectedOver: Double

    /// Builds a forecast, or returns nil when there's no positive limit.
    /// - Parameters:
    ///   - resetDay: day of the month the spend resets (1–31; clamped to shorter months).
    public static func make(
        used: Double, limit: Double, resetDay: Int, now: Date = Date(), calendar: Calendar = .current
    ) -> MonthlyForecast? {
        guard limit > 0, used.isFinite else { return nil }
        let (start, end) = period(resetDay: resetDay, containing: now, calendar: calendar)
        let today = calendar.startOfDay(for: now)
        guard let tomorrow = calendar.date(byAdding: .day, value: 1, to: today) else { return nil }

        let left = workingDays(from: today, to: end, calendar: calendar)
        let elapsed = workingDays(from: start, to: tomorrow, calendar: calendar)
        let todayIsWorkingDay = isWorkingDay(today, calendar: calendar)
        let leftAfterToday = max(left - (todayIsWorkingDay ? 1 : 0), 0)

        let averagePerDay = used / Double(max(elapsed, 1))
        let projected = used + averagePerDay * Double(leftAfterToday)
        let remaining = max(limit - used, 0)

        let status: Status
        if projected > limit {
            status = .over
        } else if projected > limit * 0.9 {
            status = .close
        } else {
            status = .onTrack
        }

        return MonthlyForecast(
            periodStart: start,
            resetsAt: end,
            workingDaysLeft: left,
            workingDaysElapsed: elapsed,
            remaining: remaining,
            perWorkingDay: left > 0 ? remaining / Double(left) : nil,
            projected: projected,
            status: status,
            projectedOver: max(projected - limit, 0)
        )
    }

    /// The monthly period containing `date`: from the most recent reset day (at local midnight)
    /// to the next one. A reset day past the end of a short month falls on its last day.
    public static func period(resetDay: Int, containing date: Date, calendar: Calendar = .current) -> (start: Date, end: Date) {
        let day = min(max(resetDay, 1), 31)
        let thisMonth = resetDate(day: day, inMonthOf: date, calendar: calendar)
        if date >= thisMonth {
            let next = calendar.date(byAdding: .month, value: 1, to: firstOfMonth(date, calendar))!
            return (thisMonth, resetDate(day: day, inMonthOf: next, calendar: calendar))
        } else {
            let previous = calendar.date(byAdding: .month, value: -1, to: firstOfMonth(date, calendar))!
            return (resetDate(day: day, inMonthOf: previous, calendar: calendar), thisMonth)
        }
    }

    /// Monday–Friday days in [`from`, `to`), counted by calendar day.
    public static func workingDays(from: Date, to: Date, calendar: Calendar = .current) -> Int {
        var count = 0
        var day = calendar.startOfDay(for: from)
        while day < to {
            if isWorkingDay(day, calendar: calendar) { count += 1 }
            guard let next = calendar.date(byAdding: .day, value: 1, to: day) else { break }
            day = next
        }
        return count
    }

    /// Monday to Friday, whatever the region's weekend is.
    public static func isWorkingDay(_ date: Date, calendar: Calendar = .current) -> Bool {
        var gregorian = Calendar(identifier: .gregorian)
        gregorian.timeZone = calendar.timeZone
        let weekday = gregorian.component(.weekday, from: date)  // 1 = Sunday, 7 = Saturday
        return weekday != 1 && weekday != 7
    }

    private static func firstOfMonth(_ date: Date, _ calendar: Calendar) -> Date {
        calendar.date(from: calendar.dateComponents([.year, .month], from: date))!
    }

    private static func resetDate(day: Int, inMonthOf date: Date, calendar: Calendar) -> Date {
        let first = firstOfMonth(date, calendar)
        let daysInMonth = calendar.range(of: .day, in: .month, for: first)?.count ?? 28
        return calendar.date(byAdding: .day, value: min(day, daysInMonth) - 1, to: first)!
    }
}

extension PlanUsage {
    /// Personal plans whose monthly figure is a usage-credits cap, not an organisation spend limit.
    static let personalPlans: Set<String> = ["free", "pro", "max"]

    /// True when the monthly figure is a spend limit set for the account (Enterprise and other
    /// organisation plans), so a reset date and forecast make sense.
    public var hasMonthlySpendLimit: Bool {
        guard let monthly, let limit = monthly.limit, limit > 0, monthly.isEnabled else { return false }
        if let plan = subscriptionType?.lowercased(), Self.personalPlans.contains(plan) { return false }
        return true
    }

    /// The forecast for the monthly spend limit, when the account has one.
    public func monthlyForecast(resetDay: Int, now: Date = Date(), calendar: Calendar = .current) -> MonthlyForecast? {
        guard hasMonthlySpendLimit, let monthly, let limit = monthly.limit else { return nil }
        return MonthlyForecast.make(used: monthly.used, limit: limit, resetDay: resetDay, now: now, calendar: calendar)
    }
}
