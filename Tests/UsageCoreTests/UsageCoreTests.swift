import XCTest
@testable import UsageCore

final class UsagePeriodTests: XCTestCase {
    private var calendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "Europe/London")!
        return c
    }()

    private func date(_ y: Int, _ m: Int, _ d: Int, _ h: Int, _ min: Int = 0) -> Date {
        calendar.date(from: DateComponents(year: y, month: m, day: d, hour: h, minute: min))!
    }

    func testAfterResetHourStartsToday() {
        let period = UsagePeriod.current(now: date(2026, 11, 16, 15, 30), resetHour: 8, calendar: calendar)
        XCTAssertEqual(period.start, date(2026, 11, 16, 8))
        XCTAssertEqual(period.end, date(2026, 11, 17, 8))
    }

    func testBeforeResetHourStartsYesterday() {
        let period = UsagePeriod.current(now: date(2026, 11, 16, 7, 59), resetHour: 8, calendar: calendar)
        XCTAssertEqual(period.start, date(2026, 11, 15, 8))
        XCTAssertEqual(period.end, date(2026, 11, 16, 8))
    }

    func testExactlyAtResetHourStartsNewPeriod() {
        let period = UsagePeriod.current(now: date(2026, 11, 16, 8), resetHour: 8, calendar: calendar)
        XCTAssertEqual(period.start, date(2026, 11, 16, 8))
    }

    func testDSTChangeKeepsWallClockHour() {
        // UK clocks go back on 25 Oct 2026; that day is 25 hours long.
        let period = UsagePeriod.current(now: date(2026, 10, 24, 12), resetHour: 8, calendar: calendar)
        XCTAssertEqual(period.end, date(2026, 10, 25, 8))
        XCTAssertEqual(calendar.component(.hour, from: period.end), 8)
    }
}

final class PricingTests: XCTestCase {
    func testOpus55Cost() throws {
        let usage = TokenUsage(input: 1_000_000, output: 1_000_000, cacheWrite5m: 1_000_000,
                               cacheWrite1h: 1_000_000, cacheRead: 1_000_000)
        // 4 + 20 + 5 + 8 + 0.20
        let cost = try XCTUnwrap(Pricing.cost(model: "claude-opus-5-5", usage: usage))
        XCTAssertEqual(cost, 37.20, accuracy: 0.0001)
    }

    func testSpecificIdsMatchBeforePrefixes() {
        XCTAssertEqual(Pricing.price(for: "claude-opus-5-5")?.input, 4)
        XCTAssertEqual(Pricing.price(for: "claude-opus-5")?.input, 5)
        XCTAssertEqual(Pricing.price(for: "claude-opus-4-1-20250805")?.input, 15)
        XCTAssertEqual(Pricing.price(for: "claude-sonnet-4-5-20250929")?.input, 3)
        XCTAssertEqual(Pricing.price(for: "claude-sonnet-5-5")?.cacheRead ?? 0, 0.10, accuracy: 1e-9)
        XCTAssertEqual(Pricing.price(for: "claude-fable-5-2")?.cacheRead ?? 0, 0.20, accuracy: 1e-9)
    }

    func testFastModeDoublesPrice() throws {
        let usage = TokenUsage(input: 1_000_000)
        XCTAssertEqual(try XCTUnwrap(Pricing.cost(model: "claude-opus-5-5", usage: usage, fastMode: true)), 8, accuracy: 1e-9)
    }

    func testHaiku55LongPromptRate() throws {
        let short = TokenUsage(input: 100_000)
        let long = TokenUsage(input: 200_000)
        XCTAssertEqual(try XCTUnwrap(Pricing.cost(model: "claude-haiku-5-5", usage: short)), 0.01, accuracy: 1e-9)
        XCTAssertEqual(try XCTUnwrap(Pricing.cost(model: "claude-haiku-5-5", usage: long)), 0.10, accuracy: 1e-9)
    }

    func testUnknownModelIsUnpriced() {
        XCTAssertNil(Pricing.cost(model: "<synthetic>", usage: TokenUsage(input: 10)))
    }
}

final class UsageScannerTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("projects/demo/sub"),
                                                withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func line(id: String, req: String, time: String, model: String = "claude-opus-5-5",
                      input: Int = 0, output: Int = 0, write1h: Int = 0, read: Int = 0, speed: String = "standard") -> String {
        """
        {"type":"assistant","timestamp":"\(time)","requestId":"\(req)","message":{"id":"\(id)","model":"\(model)","usage":{"input_tokens":\(input),"output_tokens":\(output),"cache_creation_input_tokens":\(write1h),"cache_read_input_tokens":\(read),"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":\(write1h)},"speed":"\(speed)"}}}
        """
    }

    func testSummarizesDedupesAndFiltersByPeriod() throws {
        let lines = [
            #"{"type":"user","timestamp":"2026-11-16T09:00:00.000Z","message":{"role":"user","content":"hi"}}"#,
            // Streaming duplicates of one response: only the final one (most output) counts.
            line(id: "msg_1", req: "req_1", time: "2026-11-16T09:00:01.000Z", input: 1_000_000, output: 10),
            line(id: "msg_1", req: "req_1", time: "2026-11-16T09:00:02.000Z", input: 1_000_000, output: 1_000_000),
            // Before the period: ignored.
            line(id: "msg_0", req: "req_0", time: "2026-11-16T07:59:59.000Z", input: 5_000_000),
            "not json",
        ]
        try lines.joined(separator: "\n").write(to: root.appendingPathComponent("projects/demo/a.jsonl"),
                                                atomically: true, encoding: .utf8)
        // Subagent logs live in nested folders.
        try line(id: "msg_2", req: "req_2", time: "2026-11-16T10:00:00Z", model: "claude-sonnet-5-5", write1h: 1_000_000, read: 1_000_000)
            .write(to: root.appendingPathComponent("projects/demo/sub/b.jsonl"), atomically: true, encoding: .utf8)

        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let now = utc.date(from: DateComponents(year: 2026, month: 11, day: 16, hour: 12))!
        let period = UsagePeriod.current(now: now, resetHour: 8, calendar: utc)

        // Test files are freshly written, so modification time is "now" (after period start).
        let summary = UsageScanner(roots: [root]).summarize(period: period)

        XCTAssertEqual(summary.requests, 2)
        XCTAssertEqual(summary.tokens.input, 1_000_000)
        XCTAssertEqual(summary.tokens.output, 1_000_000)
        XCTAssertEqual(summary.tokens.cacheWrite1h, 1_000_000)
        // Opus 5.5: $4 + $20; Sonnet 5.5: $4 (1h write) + $0.10 (read)
        XCTAssertEqual(summary.cost, 28.10, accuracy: 1e-6)
        XCTAssertEqual(summary.byModel.first?.model, "claude-opus-5-5")
        XCTAssertEqual(summary.percentUsed(ofBudget: 56.20), 50, accuracy: 1e-6)
    }

    func testDefaultRootsHonourClaudeConfigDir() {
        let roots = UsageScanner.defaultRoots(environment: ["CLAUDE_CONFIG_DIR": "/a, /b"])
        XCTAssertEqual(roots.map(\.path), ["/a", "/b"])
    }
}
