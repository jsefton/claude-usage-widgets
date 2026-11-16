import Foundation

/// Totals for one model within a period.
public struct ModelSummary: Equatable, Sendable, Identifiable {
    public var model: String
    public var tokens = TokenUsage()
    public var cost: Double = 0
    public var requests = 0
    public var id: String { model }
}

/// Aggregated usage for a period.
public struct UsageSummary: Equatable, Sendable {
    public var period: UsagePeriod
    public var tokens = TokenUsage()
    public var cost: Double = 0
    public var requests = 0
    /// Responses from models with no known price (counted in tokens, not in cost).
    public var unpricedRequests = 0
    public var byModel: [ModelSummary] = []

    public init(period: UsagePeriod) { self.period = period }

    public func percentUsed(ofBudget budget: Double) -> Double {
        guard budget > 0 else { return 0 }
        return cost / budget * 100
    }
}

/// Reads Claude Code's local transcript logs (`<config dir>/projects/**/*.jsonl`) and totals the
/// token usage recorded on assistant messages.
public struct UsageScanner: Sendable {
    public var roots: [URL]

    public init(roots: [URL]) { self.roots = roots }

    /// Default Claude Code data directories: `$CLAUDE_CONFIG_DIR` (comma separated) if set,
    /// otherwise `~/.claude` and `~/.config/claude`.
    public static func defaultRoots(environment: [String: String] = ProcessInfo.processInfo.environment) -> [URL] {
        if let custom = environment["CLAUDE_CONFIG_DIR"], !custom.isEmpty {
            return custom.split(separator: ",").map {
                URL(fileURLWithPath: ($0.trimmingCharacters(in: .whitespaces) as NSString).expandingTildeInPath)
            }
        }
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [home.appendingPathComponent(".claude"),
                home.appendingPathComponent(".config/claude")]
    }

    public func summarize(period: UsagePeriod) -> UsageSummary {
        var entries: [String: Entry] = [:]
        var anonymous: [Entry] = []

        for file in logFiles(modifiedSince: period.start) {
            guard let data = try? Data(contentsOf: file, options: .mappedIfSafe) else { continue }
            for line in data.split(separator: UInt8(ascii: "\n")) {
                guard let entry = Self.parse(line: line), period.contains(entry.timestamp) else { continue }
                if let key = entry.dedupeKey {
                    // Claude Code writes the same response several times while streaming;
                    // keep the copy with the most output tokens (the final one).
                    if let existing = entries[key], existing.usage.output >= entry.usage.output { continue }
                    entries[key] = entry
                } else {
                    anonymous.append(entry)
                }
            }
        }

        var summary = UsageSummary(period: period)
        var models: [String: ModelSummary] = [:]
        for entry in Array(entries.values) + anonymous {
            summary.tokens += entry.usage
            summary.requests += 1
            var model = models[entry.model] ?? ModelSummary(model: entry.model)
            model.tokens += entry.usage
            model.requests += 1
            if let cost = Pricing.cost(model: entry.model, usage: entry.usage, fastMode: entry.fastMode) {
                summary.cost += cost
                model.cost += cost
            } else {
                summary.unpricedRequests += 1
            }
            models[entry.model] = model
        }
        summary.byModel = models.values
            .filter { $0.tokens.total > 0 }
            .sorted { ($0.cost, $0.tokens.total) > ($1.cost, $1.tokens.total) }
        return summary
    }

    // MARK: - Files

    func logFiles(modifiedSince cutoff: Date) -> [URL] {
        let fm = FileManager.default
        var result: [URL] = []
        var seen = Set<String>()
        for root in roots {
            let projects = root.appendingPathComponent("projects")
            guard let enumerator = fm.enumerator(at: projects,
                                                 includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
                                                 options: [.skipsHiddenFiles]) else { continue }
            for case let url as URL in enumerator where url.pathExtension == "jsonl" {
                let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey])
                guard values?.isRegularFile == true else { continue }
                // A file untouched since the period began can't contain entries from this period.
                if let modified = values?.contentModificationDate, modified < cutoff { continue }
                let path = url.resolvingSymlinksInPath().path
                if seen.insert(path).inserted { result.append(url) }
            }
        }
        return result
    }

    // MARK: - Parsing

    struct Entry {
        var timestamp: Date
        var model: String
        var usage: TokenUsage
        var fastMode: Bool
        var dedupeKey: String?
    }

    private struct Line: Decodable {
        struct Message: Decodable {
            struct Usage: Decodable {
                struct CacheCreation: Decodable {
                    var ephemeral_5m_input_tokens: Int?
                    var ephemeral_1h_input_tokens: Int?
                }
                var input_tokens: Int?
                var output_tokens: Int?
                var cache_creation_input_tokens: Int?
                var cache_read_input_tokens: Int?
                var cache_creation: CacheCreation?
                var speed: String?
            }
            var id: String?
            var model: String?
            var usage: Usage?
        }
        var timestamp: String?
        var requestId: String?
        var message: Message?
    }

    static func parse(line: Data.SubSequence) -> Entry? {
        // Cheap pre-filter: most lines (user turns, tool results) carry no usage block.
        guard line.count > 20, line.range(of: Data("\"usage\"".utf8)) != nil else { return nil }
        guard let decoded = try? decoder.decode(Line.self, from: Data(line)),
              let message = decoded.message, let usage = message.usage,
              let stamp = decoded.timestamp, let timestamp = parseDate(stamp) else { return nil }

        var tokens = TokenUsage(input: usage.input_tokens ?? 0,
                                output: usage.output_tokens ?? 0,
                                cacheRead: usage.cache_read_input_tokens ?? 0)
        let created = usage.cache_creation_input_tokens ?? 0
        if let split = usage.cache_creation {
            tokens.cacheWrite5m = split.ephemeral_5m_input_tokens ?? 0
            tokens.cacheWrite1h = split.ephemeral_1h_input_tokens ?? 0
            // Anything not attributed to a TTL is billed at the default 5-minute rate.
            tokens.cacheWrite5m += max(0, created - tokens.cacheWrite5m - tokens.cacheWrite1h)
        } else {
            tokens.cacheWrite5m = created
        }

        let key: String?
        switch (message.id, decoded.requestId) {
        case let (id?, req?): key = "\(id):\(req)"
        case let (id?, nil): key = id
        default: key = nil
        }
        return Entry(timestamp: timestamp,
                     model: message.model ?? "unknown",
                     usage: tokens,
                     fastMode: usage.speed == "fast",
                     dedupeKey: key)
    }

    private static let decoder = JSONDecoder()
    private static let fractionalFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let plainFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parseDate(_ string: String) -> Date? {
        fractionalFormatter.date(from: string) ?? plainFormatter.date(from: string)
    }
}
