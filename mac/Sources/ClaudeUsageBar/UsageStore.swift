import Foundation
import UsageCore

/// UserDefaults keys shared by the views (@AppStorage) and the store.
enum SettingsKey {
    static let dailyBudget = "dailyBudget"
    static let resetHour = "resetHour"
    static let displayMode = "displayMode"
    static let customDataDir = "customDataDir"
    static let showPlanUsage = "showPlanUsage"
    static let monthlyResetDay = "monthlyResetDay"
}

enum Defaults {
    static let dailyBudget = 25.0
    static let resetHour = 8
    /// Day of the month an Enterprise monthly spend limit resets.
    static let monthlyResetDay = 1
}

/// What the menu bar text shows.
enum DisplayMode: String, CaseIterable, Identifiable {
    case costAndPercent, cost, percent
    var id: String { rawValue }
    var label: String {
        switch self {
        case .costAndPercent: return "Cost and %"
        case .cost: return "Cost only"
        case .percent: return "% only"
        }
    }
}

@MainActor
final class UsageStore: ObservableObject {
    @Published private(set) var summary: UsageSummary?
    @Published private(set) var lastUpdated: Date?
    @Published private(set) var isRefreshing = false
    @Published private(set) var dataDirectoryFound = true

    /// Usage reported by Claude for the signed-in account (monthly spend, plan limits).
    @Published private(set) var plan: PlanUsage?
    @Published private(set) var planError: String?
    private var planFetchedAt: Date?
    private var isFetchingPlan = false
    /// How often to ask Claude for plan usage (Claude Code itself polls about every 5 minutes).
    private let planInterval: TimeInterval = 300

    private var timer: Timer?
    private var pendingRefresh = false
    private let refreshInterval: TimeInterval = 60

    init() {
        refresh()
        let timer = Timer(timeInterval: refreshInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        timer.tolerance = 5
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    var dailyBudget: Double {
        let value = UserDefaults.standard.double(forKey: SettingsKey.dailyBudget)
        return value > 0 ? value : Defaults.dailyBudget
    }

    var resetHour: Int {
        UserDefaults.standard.object(forKey: SettingsKey.resetHour) as? Int ?? Defaults.resetHour
    }

    var showPlanUsage: Bool {
        UserDefaults.standard.object(forKey: SettingsKey.showPlanUsage) as? Bool ?? true
    }

    var roots: [URL] {
        let custom = (UserDefaults.standard.string(forKey: SettingsKey.customDataDir) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !custom.isEmpty {
            return custom.split(separator: ",").map {
                URL(fileURLWithPath: ($0.trimmingCharacters(in: .whitespaces) as NSString).expandingTildeInPath)
            }
        }
        return UsageScanner.defaultRoots()
    }

    /// Refreshes the local daily total, and the plan usage from Claude when it is due.
    /// - Parameter force: fetch plan usage now (still at most every 30 seconds), e.g. the refresh button.
    func refresh(force: Bool = false) {
        refreshPlan(force: force)
        guard !isRefreshing else { pendingRefresh = true; return }
        isRefreshing = true
        let period = UsagePeriod.current(resetHour: resetHour)
        let roots = self.roots
        Task.detached(priority: .utility) {
            let found = roots.contains {
                FileManager.default.fileExists(atPath: $0.appendingPathComponent("projects").path)
            }
            let summary = UsageScanner(roots: roots).summarize(period: period)
            await MainActor.run {
                self.summary = summary
                self.dataDirectoryFound = found
                self.lastUpdated = Date()
                self.isRefreshing = false
                if self.pendingRefresh {
                    self.pendingRefresh = false
                    self.refresh()
                }
            }
        }
    }
}

extension UsageStore {
    func refreshPlan(force: Bool = false) {
        guard showPlanUsage else {
            plan = nil
            planError = nil
            planFetchedAt = nil
            return
        }
        guard !isFetchingPlan else { return }
        if let fetched = planFetchedAt,
           Date().timeIntervalSince(fetched) < (force ? 30 : planInterval) { return }

        isFetchingPlan = true
        Task {
            do {
                let usage = try await ClaudeAccount.fetchPlanUsage()
                plan = usage
                planError = nil
            } catch let error as ClaudeAccount.FetchError {
                planError = error.message
            } catch {
                planError = error.localizedDescription
            }
            planFetchedAt = Date()
            isFetchingPlan = false
        }
    }
}

enum Format {
    static func cost(_ value: Double) -> String {
        String(format: "$%.2f", value)
    }

    static func money(_ value: Double, currency: String, decimalPlaces: Int = 2) -> String {
        value.formatted(
            .currency(code: currency).precision(.fractionLength(decimalPlaces))
        )
    }

    /// "Mon 1 Dec".
    static func day(_ date: Date) -> String {
        date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
    }

    /// "1st", "2nd", "23rd".
    static func ordinal(_ value: Int) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .ordinal
        return formatter.string(from: NSNumber(value: value)) ?? "\(value)"
    }

    /// "20:30" for times in the next 24 hours, otherwise "Wed 18 Nov".
    static func reset(_ date: Date) -> String {
        if date.timeIntervalSinceNow < 24 * 3600 {
            return date.formatted(date: .omitted, time: .shortened)
        }
        return date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
    }

    static func percent(_ value: Double) -> String {
        String(format: "%.0f%%", value)
    }

    static func tokens(_ value: Int) -> String {
        switch value {
        case 1_000_000_000...: return String(format: "%.2fB", Double(value) / 1_000_000_000)
        case 1_000_000...: return String(format: "%.1fM", Double(value) / 1_000_000)
        case 1_000...: return String(format: "%.1fK", Double(value) / 1_000)
        default: return "\(value)"
        }
    }

    static func hour(_ hour: Int) -> String {
        var components = DateComponents()
        components.hour = hour
        components.minute = 0
        let date = Calendar.current.date(from: components) ?? Date()
        return date.formatted(date: .omitted, time: .shortened)
    }
}
