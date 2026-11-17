import Foundation

/// Usage figures reported by Claude itself for the signed-in account: the monthly spend limit
/// (Enterprise spend limits, or usage credits on Pro/Max) and the plan's limit meters
/// (5-hour session, weekly, ...).
///
/// Parsed from `GET https://api.anthropic.com/api/oauth/usage`, the endpoint behind Claude
/// Code's `/usage` screen. It is undocumented, so every field is optional and anything
/// unexpected is skipped rather than treated as an error.
public struct PlanUsage: Equatable, Sendable {
    public var monthly: MonthlySpend?
    public var limits: [PlanLimit]
    /// The login's plan from Claude Code's saved credentials, e.g. "max" or "enterprise".
    /// Not part of the usage response; set by whoever fetched it.
    public var subscriptionType: String?

    public init(monthly: MonthlySpend?, limits: [PlanLimit], subscriptionType: String? = nil) {
        self.monthly = monthly
        self.limits = limits
        self.subscriptionType = subscriptionType
    }
}

/// Money spent this billing period against a monthly cap.
public struct MonthlySpend: Equatable, Sendable {
    /// Amount used, in major units (e.g. dollars, pounds).
    public var used: Double
    /// The monthly cap in major units, or nil when there is no cap.
    public var limit: Double?
    /// ISO 4217 code, e.g. "USD" or "GBP".
    public var currency: String
    /// Decimal places the server uses for this currency.
    public var decimalPlaces: Int
    /// Share of the cap used, 0–100+, or nil when there is no cap.
    public var percent: Double?
    /// When the monthly period resets, if the server says.
    public var resetsAt: Date?
    /// False while this spend can't be used (e.g. out of credits).
    public var isEnabled: Bool
    /// The server's reason when not enabled, e.g. "out_of_credits".
    public var disabledReason: String?
    /// The server's reading for colouring: "normal", "warning" or "critical".
    public var severity: String?

    public init(
        used: Double, limit: Double?, currency: String, decimalPlaces: Int = 2, percent: Double?,
        resetsAt: Date? = nil, isEnabled: Bool = true, disabledReason: String? = nil, severity: String? = nil
    ) {
        self.used = used
        self.limit = limit
        self.currency = currency
        self.decimalPlaces = decimalPlaces
        self.percent = percent
        self.resetsAt = resetsAt
        self.isEnabled = isEnabled
        self.disabledReason = disabledReason
        self.severity = severity
    }
}

/// One of the plan's limit meters, e.g. the 5-hour session or the weekly limit.
public struct PlanLimit: Equatable, Sendable, Identifiable {
    public var id: String { "\(kind)|\(label)" }
    /// The server's meter kind, e.g. "session", "weekly_all", "weekly_scoped".
    public var kind: String
    /// The server's row group, e.g. "session", "weekly", "monthly".
    public var group: String
    /// Human-readable label built from the kind and scope.
    public var label: String
    /// Share of the window used, 0–100.
    public var percent: Double
    public var resetsAt: Date?
    /// "normal", "warning" or "critical".
    public var severity: String?

    public init(kind: String, group: String, label: String, percent: Double, resetsAt: Date?, severity: String?) {
        self.kind = kind
        self.group = group
        self.label = label
        self.percent = percent
        self.resetsAt = resetsAt
        self.severity = severity
    }
}

extension PlanUsage {
    /// Parses the usage endpoint's JSON body. Returns nil only when the body isn't a JSON object.
    public static func parse(_ data: Data) -> PlanUsage? {
        guard let raw = try? JSONDecoder().decode(RawUsage.self, from: data) else { return nil }

        let limits: [PlanLimit] = (raw.limits ?? []).compactMap { row in
            guard let kind = row.kind, let percent = row.percent else { return nil }
            let group = row.group ?? kind
            return PlanLimit(
                kind: kind,
                group: group,
                label: label(kind: kind, group: group, scope: row.scope),
                percent: percent,
                resetsAt: parseDate(row.resets_at),
                severity: row.severity
            )
        }

        // A "monthly" meter, when present, carries the monthly period's reset date.
        let monthlyReset = limits.first { $0.group == "monthly" }?.resetsAt

        var monthly: MonthlySpend?
        if let spend = raw.spend, let used = spend.used, let usedMajor = used.major {
            // Newer, unit-explicit shape.
            let limit = spend.limit?.major ?? spend.cap?.money?.major
            monthly = MonthlySpend(
                used: usedMajor,
                limit: limit,
                currency: used.currency ?? spend.limit?.currency ?? "USD",
                decimalPlaces: used.exponent ?? 2,
                percent: spend.percent ?? limit.flatMap { $0 > 0 ? usedMajor / $0 * 100 : nil },
                resetsAt: monthlyReset,
                isEnabled: spend.enabled ?? true,
                disabledReason: spend.disabled_reason,
                severity: spend.severity
            )
        } else if let extra = raw.extra_usage, let usedMinor = extra.used_credits {
            // Older shape: amounts in minor units of `currency` (cents for USD).
            let places = extra.decimal_places ?? 2
            let scale = pow(10, Double(places))
            let limit = extra.monthly_limit.map { $0 / scale }
            monthly = MonthlySpend(
                used: usedMinor / scale,
                limit: limit,
                currency: extra.currency?.uppercased() ?? "USD",
                decimalPlaces: places,
                percent: extra.utilization,
                resetsAt: monthlyReset,
                isEnabled: extra.is_enabled ?? true,
                disabledReason: extra.disabled_reason
            )
        }

        return PlanUsage(monthly: monthly, limits: limits)
    }

    static func label(kind: String, group: String, scope: RawUsage.Scope?) -> String {
        let scoped = scope?.model?.display_name ?? scope?.surface?.display_name
        let base: String
        switch kind {
        case "session": base = "5-hour session"
        case "weekly_all": base = "Weekly"
        case "weekly_scoped": base = "Weekly"
        default:
            base = group.replacingOccurrences(of: "_", with: " ").capitalized
        }
        if let scoped, !scoped.isEmpty { return "\(base) · \(scoped)" }
        return base
    }

    /// Parses ISO 8601 timestamps, with or without fractional seconds (the server sends microseconds).
    public static func parseDate(_ string: String?) -> Date? {
        guard let string, !string.isEmpty else { return nil }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        // ISO8601DateFormatter only handles up to milliseconds, so trim any longer fraction.
        let trimmed = string.replacingOccurrences(
            of: #"(\.\d{3})\d+"#, with: "$1", options: .regularExpression
        )
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: trimmed) ?? plain.date(from: trimmed)
    }
}

/// Wire format. Field names match the JSON; everything optional because the endpoint is undocumented.
struct RawUsage: Decodable {
    struct Money: Decodable {
        var amount_minor: Double?
        var currency: String?
        var exponent: Int?
        var major: Double? {
            guard let amount_minor else { return nil }
            return amount_minor / pow(10, Double(exponent ?? 2))
        }
    }

    struct Cap: Decodable {
        var money: Money?
    }

    struct Spend: Decodable {
        var used: Money?
        var limit: Money?
        var cap: Cap?
        var percent: Double?
        var severity: String?
        var enabled: Bool?
        var disabled_reason: String?
    }

    struct ExtraUsage: Decodable {
        var is_enabled: Bool?
        var monthly_limit: Double?
        var used_credits: Double?
        var utilization: Double?
        var currency: String?
        var decimal_places: Int?
        var disabled_reason: String?
    }

    struct Scope: Decodable {
        struct Named: Decodable { var display_name: String? }
        var model: Named?
        var surface: Named?
    }

    struct Limit: Decodable {
        var kind: String?
        var group: String?
        var percent: Double?
        var severity: String?
        var resets_at: String?
        var scope: Scope?
    }

    var spend: Spend?
    var extra_usage: ExtraUsage?
    var limits: [Limit]?
}
