import Foundation
import UsageCore

/// UserDefaults keys shared by the views (@AppStorage) and the store.
enum SettingsKey {
    static let dailyBudget = "dailyBudget"
    static let resetHour = "resetHour"
    static let displayMode = "displayMode"
    static let customDataDir = "customDataDir"
}

enum Defaults {
    static let dailyBudget = 50.0
    static let resetHour = 8
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

    func refresh() {
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

enum Format {
    static func cost(_ value: Double) -> String {
        String(format: "$%.2f", value)
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
