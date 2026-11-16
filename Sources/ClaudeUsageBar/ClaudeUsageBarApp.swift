import AppKit
import SwiftUI
import UsageCore

@main
struct ClaudeUsageBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var store = UsageStore()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(store)
        } label: {
            MenuBarLabel()
                .environmentObject(store)
        }
        .menuBarExtraStyle(.window)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Menu bar only: no Dock icon even when launched via `swift run` (the bundle also sets LSUIElement).
        NSApp.setActivationPolicy(.accessory)
    }
}

/// The text shown in the macOS menu bar, e.g. "$12.34 · 25%".
struct MenuBarLabel: View {
    @EnvironmentObject private var store: UsageStore
    @AppStorage(SettingsKey.dailyBudget) private var dailyBudget = Defaults.dailyBudget
    @AppStorage(SettingsKey.displayMode) private var displayMode = DisplayMode.costAndPercent.rawValue

    var body: some View {
        Text(text)
            .monospacedDigit()
    }

    private var text: String {
        guard let summary = store.summary else { return "Claude …" }
        let cost = Format.cost(summary.cost)
        let percent = Format.percent(summary.percentUsed(ofBudget: dailyBudget))
        switch DisplayMode(rawValue: displayMode) ?? .costAndPercent {
        case .costAndPercent: return "\(cost) · \(percent)"
        case .cost: return cost
        case .percent: return percent
        }
    }
}
