import Foundation

/// A "usage day" that starts at a configurable local hour (default 08:00) rather than midnight.
public struct UsagePeriod: Equatable, Sendable {
    public let start: Date
    public let end: Date

    public func contains(_ date: Date) -> Bool { date >= start && date < end }

    /// The period containing `now`. Before the reset hour, the period started yesterday at the reset hour.
    public static func current(now: Date = Date(), resetHour: Int = 8, calendar: Calendar = .current) -> UsagePeriod {
        let hour = min(max(resetHour, 0), 23)
        let todayStart = calendar.date(bySettingHour: hour, minute: 0, second: 0, of: now)
            ?? calendar.startOfDay(for: now)
        let start = now >= todayStart
            ? todayStart
            : (calendar.date(byAdding: .day, value: -1, to: todayStart) ?? todayStart.addingTimeInterval(-86_400))
        // Add a calendar day (not 86 400 s) so DST changes keep the reset at the same wall-clock hour.
        let nextDay = calendar.date(byAdding: .day, value: 1, to: start) ?? start.addingTimeInterval(86_400)
        let end = calendar.date(bySettingHour: hour, minute: 0, second: 0, of: nextDay) ?? nextDay
        return UsagePeriod(start: start, end: end)
    }
}
