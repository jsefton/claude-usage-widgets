import AppKit
import ServiceManagement
import SwiftUI
import UsageCore

/// The panel that drops down when the menu bar item is clicked.
struct MenuContentView: View {
    @EnvironmentObject private var store: UsageStore
    @AppStorage(SettingsKey.dailyBudget) private var dailyBudget = Defaults.dailyBudget
    @AppStorage(SettingsKey.resetHour) private var resetHour = Defaults.resetHour
    @AppStorage(SettingsKey.showPlanUsage) private var showPlanUsage = true
    @AppStorage(SettingsKey.monthlyResetDay) private var monthlyResetDay = Defaults.monthlyResetDay
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
                if showPlanUsage {
                    Divider()
                    planSection
                }
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
            DisclosureGroup(isExpanded: $showSettings) {
                SettingsView().padding(.top, 6)
            } label: {
                // Make the whole "Settings" row clickable, not just the chevron.
                // No withAnimation here: animating the height change inside a MenuBarExtra
                // window leaves the panel mis-sized and mis-positioned, clipping the content.
                Text("Settings")
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                    .onTapGesture { showSettings.toggle() }
            }
            footer
        }
        .padding(14)
        .frame(width: 320)
        .background(PinTopEdge())
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

    /// Usage as Claude reports it for the signed-in account: monthly spend and plan limits.
    @ViewBuilder
    private var planSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Claude plan").font(.caption).foregroundStyle(.secondary)
            if let plan = store.plan {
                if let monthly = plan.monthly {
                    let forecast = plan.monthlyForecast(resetDay: monthlyResetDay)
                    monthlyRow(monthly, forecast: forecast)
                    if let forecast, let limit = monthly.limit {
                        forecastRows(forecast, monthly: monthly, limit: limit)
                    }
                }
                ForEach(plan.limits) { limit in
                    meterRow(
                        title: limit.label,
                        value: Format.percent(limit.percent),
                        percent: limit.percent,
                        severity: limit.severity,
                        footnote: limit.resetsAt.map { "Resets \(Format.reset($0))" }
                    )
                }
                if plan.monthly == nil && plan.limits.isEmpty {
                    Text("No plan limits reported for this account.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } else if store.planError == nil {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity)
            }
            if let error = store.planError {
                Label(error, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func monthlyRow(_ monthly: MonthlySpend, forecast: MonthlyForecast?) -> some View {
        let used = Format.money(monthly.used, currency: monthly.currency, decimalPlaces: monthly.decimalPlaces)
        let value: String
        if let limit = monthly.limit {
            value = "\(used) of \(Format.money(limit, currency: monthly.currency, decimalPlaces: monthly.decimalPlaces))"
        } else {
            value = used
        }
        var notes: [String] = []
        if let forecast {
            notes.append("Resets \(Format.day(forecast.resetsAt))")
        } else if let resets = monthly.resetsAt {
            notes.append("Resets \(Format.reset(resets))")
        }
        if !monthly.isEnabled {
            notes.append(monthly.disabledReason == "out_of_credits" ? "Off: out of credits" : "Off")
        }
        return meterRow(
            title: "Monthly spend",
            value: value,
            percent: monthly.percent,
            severity: monthly.severity,
            footnote: notes.isEmpty ? nil : notes.joined(separator: " · "),
            showsPercent: true
        )
    }

    /// Enterprise spend limits: whether the current pace stays under the limit, and what's left per working day.
    private func forecastRows(_ forecast: MonthlyForecast, monthly: MonthlySpend, limit: Double) -> some View {
        let money = { (value: Double) in
            Format.money(value, currency: monthly.currency, decimalPlaces: monthly.decimalPlaces)
        }
        let projected = money(forecast.projected)
        let outlook: String
        let outlookColor: Color
        switch forecast.status {
        case .onTrack:
            outlook = "On track · about \(projected)"
            outlookColor = .green
        case .close:
            outlook = "Close to limit · about \(projected)"
            outlookColor = .orange
        case .over:
            outlook = "Over by about \(money(forecast.projectedOver))"
            outlookColor = .red
        }
        let days = forecast.workingDaysLeft
        let perDay = forecast.perWorkingDay.map {
            "\(money($0)) · \(days) working day\(days == 1 ? "" : "s") left"
        } ?? "No working days left"

        return Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 3) {
            GridRow {
                Text("Forecast").foregroundStyle(.secondary)
                Text(outlook).foregroundStyle(outlookColor)
            }
            GridRow {
                Text("Per working day").foregroundStyle(.secondary)
                Text(perDay)
            }
        }
        .font(.caption)
        .monospacedDigit()
        .help("Forecast: spend so far, plus the average per working day (Mon–Fri) so far for each working day "
              + "left until \(Format.day(forecast.resetsAt)). Per working day: the \(money(forecast.remaining)) left, "
              + "spread over the working days from today until the reset.")
    }

    private func meterRow(
        title: String, value: String, percent: Double?, severity: String?, footnote: String?, showsPercent: Bool = false
    ) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                Spacer()
                Text(value).monospacedDigit()
                if let percent, showsPercent {
                    Text(Format.percent(percent))
                        .monospacedDigit()
                        .foregroundStyle(color(for: percent, severity: severity))
                }
            }
            .font(.callout)
            if let percent {
                ProgressView(value: min(max(percent, 0) / 100, 1))
                    .tint(color(for: percent, severity: severity))
                    .controlSize(.small)
            }
            if let footnote {
                Text(footnote).font(.caption).foregroundStyle(.secondary)
            }
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
                store.refresh(force: true)
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

    /// Uses the server's severity when it gives one, otherwise the same thresholds as the daily bar.
    private func color(for percent: Double, severity: String?) -> Color {
        switch severity {
        case "critical": return .red
        case "warning": return .orange
        case "normal": return .green
        default: return color(for: percent)
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
    @AppStorage(SettingsKey.showPlanUsage) private var showPlanUsage = true
    @AppStorage(SettingsKey.monthlyResetDay) private var monthlyResetDay = Defaults.monthlyResetDay
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
            Toggle("Show Claude plan usage", isOn: $showPlanUsage)
                .help("Reads Claude Code's login to fetch your monthly spend and plan limits from Claude.")
            Picker("Month resets on", selection: $monthlyResetDay) {
                ForEach(1...31, id: \.self) { Text(Format.ordinal($0)).tag($0) }
            }
            .help("Day of the month your Enterprise monthly spend limit resets. Used for the reset date and forecast.")
            Toggle("Launch at login", isOn: $launchAtLogin)
            if let loginError {
                Text(loginError).font(.caption).foregroundStyle(.red)
            }
        }
        .controlSize(.small)
        .onChange(of: resetHour) { _ in store.refresh() }
        .onChange(of: showPlanUsage) { _ in store.refreshPlan(force: true) }
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
