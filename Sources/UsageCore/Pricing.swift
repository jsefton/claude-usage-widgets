import Foundation

/// Per-million-token prices in USD for one model.
public struct ModelPrice: Equatable, Sendable {
    public var input: Double
    public var output: Double
    public var cacheWrite5m: Double
    public var cacheWrite1h: Double
    public var cacheRead: Double

    public init(input: Double, output: Double, cacheWrite5m: Double, cacheWrite1h: Double, cacheRead: Double) {
        self.input = input
        self.output = output
        self.cacheWrite5m = cacheWrite5m
        self.cacheWrite1h = cacheWrite1h
        self.cacheRead = cacheRead
    }

    /// Standard rate card: 5m cache writes 1.25x input, 1h writes 2x, reads at `readMultiplier` x input.
    public static func standard(input: Double, output: Double, readMultiplier: Double = 0.1) -> ModelPrice {
        ModelPrice(input: input, output: output,
                   cacheWrite5m: input * 1.25, cacheWrite1h: input * 2,
                   cacheRead: input * readMultiplier)
    }

    func scaled(by factor: Double) -> ModelPrice {
        ModelPrice(input: input * factor, output: output * factor,
                   cacheWrite5m: cacheWrite5m * factor, cacheWrite1h: cacheWrite1h * factor,
                   cacheRead: cacheRead * factor)
    }
}

/// Token counts for a single API response (or an aggregate of many).
public struct TokenUsage: Equatable, Sendable {
    public var input: Int = 0
    public var output: Int = 0
    public var cacheWrite5m: Int = 0
    public var cacheWrite1h: Int = 0
    public var cacheRead: Int = 0

    public init(input: Int = 0, output: Int = 0, cacheWrite5m: Int = 0, cacheWrite1h: Int = 0, cacheRead: Int = 0) {
        self.input = input
        self.output = output
        self.cacheWrite5m = cacheWrite5m
        self.cacheWrite1h = cacheWrite1h
        self.cacheRead = cacheRead
    }

    public var total: Int { input + output + cacheWrite5m + cacheWrite1h + cacheRead }
    /// Everything that counts as prompt for this request.
    public var promptTokens: Int { input + cacheWrite5m + cacheWrite1h + cacheRead }

    public static func += (lhs: inout TokenUsage, rhs: TokenUsage) {
        lhs.input += rhs.input
        lhs.output += rhs.output
        lhs.cacheWrite5m += rhs.cacheWrite5m
        lhs.cacheWrite1h += rhs.cacheWrite1h
        lhs.cacheRead += rhs.cacheRead
    }
}

/// Anthropic API list prices (first-party), used to turn Claude Code token logs into dollars.
///
/// Prices last checked November 2026. Matching is by substring on the model id, first match wins,
/// so more specific ids must come before their prefixes (e.g. `opus-5-5` before `opus-5`).
public enum Pricing {
    public static let table: [(match: String, price: ModelPrice)] = [
        // Fable / Mythos
        ("fable-5-2",  .standard(input: 8,  output: 40, readMultiplier: 0.025)),
        ("mythos-5-2", .standard(input: 8,  output: 40, readMultiplier: 0.025)),
        ("fable-5-1",  .standard(input: 10, output: 50, readMultiplier: 0.025)),
        ("mythos-5-1", .standard(input: 10, output: 50, readMultiplier: 0.025)),
        ("fable-5",    .standard(input: 10, output: 50, readMultiplier: 0.1)),
        ("mythos-5",   .standard(input: 10, output: 50, readMultiplier: 0.1)),
        // Opus
        ("opus-5-5",   .standard(input: 4,  output: 20, readMultiplier: 0.05)),
        ("opus-5",     .standard(input: 5,  output: 25)),
        ("opus-4-8",   .standard(input: 5,  output: 25)),
        ("opus-4-7",   .standard(input: 5,  output: 25)),
        ("opus-4-6",   .standard(input: 5,  output: 25)),
        ("opus-4-5",   .standard(input: 5,  output: 25)),
        ("opus-4",     .standard(input: 15, output: 75)),   // Opus 4 / 4.1
        ("opus",       .standard(input: 15, output: 75)),   // Claude 3 Opus
        // Sonnet
        ("sonnet-5-5", .standard(input: 2,  output: 10, readMultiplier: 0.05)),
        ("sonnet-5",   .standard(input: 2,  output: 10)),
        ("sonnet",     .standard(input: 3,  output: 15)),   // Sonnet 4.6 / 4.5 / 4 / 3.7
        // Haiku
        ("haiku-5-5",  .standard(input: 0.10, output: 0.50)),
        ("haiku-4-5",  .standard(input: 1,  output: 5)),
        ("haiku-3-5",  .standard(input: 0.8, output: 4)),
        ("haiku",      .standard(input: 0.25, output: 1.25)),
    ]

    /// Claude Haiku 5.5 bills prompts over 100K tokens at a higher rate card.
    static let haiku55LongPrompt = ModelPrice.standard(input: 0.50, output: 2.50)
    static let haiku55LongPromptThreshold = 100_000

    /// Fast mode is billed at 2x the standard rate on every model that offers it.
    static let fastModeMultiplier = 2.0

    public static func price(for model: String) -> ModelPrice? {
        let id = model.lowercased()
        return table.first(where: { id.contains($0.match) })?.price
    }

    /// Cost in USD of one response. Returns nil for unknown models (e.g. `<synthetic>` placeholders).
    public static func cost(model: String, usage: TokenUsage, fastMode: Bool = false) -> Double? {
        guard var price = price(for: model) else { return nil }
        if model.lowercased().contains("haiku-5-5"), usage.promptTokens > haiku55LongPromptThreshold {
            price = haiku55LongPrompt
        }
        if fastMode { price = price.scaled(by: fastModeMultiplier) }
        let perToken = 1.0 / 1_000_000
        return (Double(usage.input) * price.input
              + Double(usage.output) * price.output
              + Double(usage.cacheWrite5m) * price.cacheWrite5m
              + Double(usage.cacheWrite1h) * price.cacheWrite1h
              + Double(usage.cacheRead) * price.cacheRead) * perToken
    }
}
