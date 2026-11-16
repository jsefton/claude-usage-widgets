import AppKit
import ServiceManagement
import SwiftUI
import UsageCore

/// The panel that drops down when the menu bar item is clicked.
struct MenuContentView: View {
    @EnvironmentObject private var store: UsageStore
    @AppStorage(SettingsKey.dailyBudget) private var dailyBudget = Defaults.dailyBudget
    @AppStorage(SettingsKey.resetHour) private var resetHour = Defaults.resetHour
    @State private var showSettings = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if !store.dataDirectoryFound {
                Label("No Claude Code logs found. Set the data folder in Settings.", systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if let summary = store.summary {
                costSection(summary)
                Divider()
                tokenSection(summary)
                if !summary.byModel.isEmpty {
                    Divider()
                    modelSection(summary)
                }
            } else {
                ProgressView().frame(maxWidth: .infinity)
            }
            Divider()
            DisclosureGroup("Settings", isExpanded: $showSettings) {
                SettingsView().padding(.top, 6)
            }
            footer
        }
        .padding(14)
        .frame(width: 320)
        .onAppear { store.refresh() }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Claude usage today").font(.headline)
            if let period = store.summary?.period {
                Text("Since \(period.start.formatted(date: .abbreviated, time: .shortened)) · resets \(period.end.formatted(date: .omitted, time: .shortened))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func costSection(_ summary: UsageSummary) -> some View {
        let percent = summary.percentUsed(ofBudget: dailyBudget)
        return VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(Format.cost(summary.cost))
                    .font(.system(size: 28, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                Spacer()
                Text(Format.percent(percent))
                    .font(.title3.weight(.medium))
                    .monospacedDigit()
                    .foregroundStyle(color(for: percent))
            }
            ProgressView(value: min(percent / 100, 1))
                .tint(color(for: percent))
            Text("of \(Format.cost(dailyBudget)) daily budget")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func tokenSection(_ summary: UsageSummary) -> some View {
        Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 4) {
            row("Requests", "\(summary.requests)")
            row("Input", Format.tokens(summary.tokens.input))
            row("Output", Format.tokens(summary.tokens.output))
            row("Cache write", Format.tokens(summary.tokens.cacheWrite5m + summary.tokens.cacheWrite1h))
            row("Cache read", Format.tokens(summary.tokens.cacheRead))
            if summary.unpricedRequests > 0 {
                row("Unpriced", "\(summary.unpricedRequests) responses")
            }
        }
        .font(.callout)
    }

    private func modelSection(_ summary: UsageSummary) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("By model").font(.caption).foregroundStyle(.secondary)
            ForEach(summary.byModel.prefix(6)) { model in
                HStack {
                    Text(model.model).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Text(Format.cost(model.cost)).monospacedDigit()
                }
                .font(.callout)
            }
        }
    }

    private var footer: some View {
        HStack {
            if let updated = store.lastUpdated {
                Text("Updated \(updated.formatted(date: .omitted, time: .shortened))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                store.refresh()
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .disabled(store.isRefreshing)
            .help("Refresh now")
            Button("Quit") { NSApplication.shared.terminate(nil) }
        }
    }

    private func row(_ title: String, _ value: String) -> some View {
        GridRow {
            Text(title).foregroundStyle(.secondary)
            Text(value).monospacedDigit()
        }
    }

    private func color(for percent: Double) -> Color {
        switch percent {
        case ..<75: return .green
        case ..<100: return .orange
        default: return .red
        }
    }
}

struct SettingsView: View {
    @EnvironmentObject private var store: UsageStore
    @AppStorage(SettingsKey.dailyBudget) private var dailyBudget = Defaults.dailyBudget
    @AppStorage(SettingsKey.resetHour) private var resetHour = Defaults.resetHour
    @AppStorage(SettingsKey.displayMode) private var displayMode = DisplayMode.costAndPercent.rawValue
    @AppStorage(SettingsKey.customDataDir) private var customDataDir = ""
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var loginError: String?

    var body: some View {
        Form {
            TextField("Daily budget ($)", value: $dailyBudget, format: .number.precision(.fractionLength(0...2)))
            Picker("Day starts at", selection: $resetHour) {
                ForEach(0..<24, id: \.self) { Text(Format.hour($0)).tag($0) }
            }
            Picker("Menu bar shows", selection: $displayMode) {
                ForEach(DisplayMode.allCases) { Text($0.label).tag($0.rawValue) }
            }
            TextField("Data folder", text: $customDataDir, prompt: Text("~/.claude (default)"))
                .onSubmit { store.refresh() }
            Toggle("Launch at login", isOn: $launchAtLogin)
            if let loginError {
                Text(loginError).font(.caption).foregroundStyle(.red)
            }
        }
        .controlSize(.small)
        .onChange(of: resetHour) { _ in store.refresh() }
        .onChange(of: launchAtLogin) { enabled in
            do {
                if enabled {
                    try SMAppService.mainApp.register()
                } else {
                    try SMAppService.mainApp.unregister()
                }
                loginError = nil
            } catch {
                loginError = "Couldn't update login item: \(error.localizedDescription)"
            }
        }
    }
}
