using UsageCore;

namespace UsageCore.Tests;

// Ported from mac/Tests/UsageCoreTests/UsageCoreTests.swift, plus Windows-specific cases.

public class UsagePeriodTests
{
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    static DateTimeOffset Date(int y, int m, int d, int h, int min = 0) =>
        UsagePeriod.ToInstant(new DateTime(y, m, d, h, min, 0), London);

    [Fact]
    public void AfterResetHourStartsToday()
    {
        var period = UsagePeriod.Current(Date(2026, 11, 16, 15, 30), resetHour: 8, London);
        Assert.Equal(Date(2026, 11, 16, 8), period.Start);
        Assert.Equal(Date(2026, 11, 17, 8), period.End);
    }

    [Fact]
    public void BeforeResetHourStartsYesterday()
    {
        var period = UsagePeriod.Current(Date(2026, 11, 16, 7, 59), resetHour: 8, London);
        Assert.Equal(Date(2026, 11, 15, 8), period.Start);
        Assert.Equal(Date(2026, 11, 16, 8), period.End);
    }

    [Fact]
    public void ExactlyAtResetHourStartsNewPeriod()
    {
        var period = UsagePeriod.Current(Date(2026, 11, 16, 8), resetHour: 8, London);
        Assert.Equal(Date(2026, 11, 16, 8), period.Start);
    }

    [Fact]
    public void DstChangeKeepsWallClockHour()
    {
        // UK clocks go back on 25 Oct 2026; that day is 25 hours long.
        var period = UsagePeriod.Current(Date(2026, 10, 24, 12), resetHour: 8, London);
        Assert.Equal(Date(2026, 10, 25, 8), period.End);
        Assert.Equal(8, TimeZoneInfo.ConvertTime(period.End, London).Hour);
        Assert.Equal(TimeSpan.FromHours(25), period.End - period.Start);
    }

    [Fact]
    public void ResetHourInSpringForwardGapUsesFirstTimeAfterGap()
    {
        // UK clocks go forward at 01:00 GMT on 29 Mar 2026, so 01:00 local doesn't exist that day.
        var now = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(1)); // 12:00 BST
        var period = UsagePeriod.Current(now, resetHour: 1, London);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), period.Start); // = 02:00 BST
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 1, 0, 0, TimeSpan.FromHours(1)), period.End);
    }

    [Fact]
    public void ResetHourInFallBackOverlapUsesFirstOccurrence()
    {
        // 01:00–02:00 happens twice in the UK on 25 Oct 2026; the first is 01:00 BST (00:00 UTC).
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var period = UsagePeriod.Current(now, resetHour: 1, London);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), period.Start);
    }
}

public class PricingTests
{
    [Fact]
    public void Opus55Cost()
    {
        var usage = new TokenUsage(Input: 1_000_000, Output: 1_000_000, CacheWrite5m: 1_000_000,
                                   CacheWrite1h: 1_000_000, CacheRead: 1_000_000);
        // 4 + 20 + 5 + 8 + 0.20
        var cost = Pricing.Cost("claude-opus-5-5", usage);
        Assert.NotNull(cost);
        Assert.Equal(37.20, cost.Value, 4);
    }

    [Fact]
    public void SpecificIdsMatchBeforePrefixes()
    {
        Assert.Equal(4, Pricing.PriceFor("claude-opus-5-5")?.Input);
        Assert.Equal(5, Pricing.PriceFor("claude-opus-5")?.Input);
        Assert.Equal(15, Pricing.PriceFor("claude-opus-4-1-20250805")?.Input);
        Assert.Equal(3, Pricing.PriceFor("claude-sonnet-4-5-20250929")?.Input);
        Assert.Equal(0.10, Pricing.PriceFor("claude-sonnet-5-5")?.CacheRead ?? 0, 9);
        Assert.Equal(0.20, Pricing.PriceFor("claude-fable-5-2")?.CacheRead ?? 0, 9);
    }

    [Fact]
    public void FastModeDoublesPrice()
    {
        var cost = Pricing.Cost("claude-opus-5-5", new TokenUsage(Input: 1_000_000), fastMode: true);
        Assert.Equal(8, cost!.Value, 9);
    }

    [Fact]
    public void Haiku55LongPromptRate()
    {
        Assert.Equal(0.01, Pricing.Cost("claude-haiku-5-5", new TokenUsage(Input: 100_000))!.Value, 9);
        Assert.Equal(0.10, Pricing.Cost("claude-haiku-5-5", new TokenUsage(Input: 200_000))!.Value, 9);
    }

    [Fact]
    public void UnknownModelIsUnpriced()
    {
        Assert.Null(Pricing.Cost("<synthetic>", new TokenUsage(Input: 10)));
    }
}

public sealed class UsageScannerTests : IDisposable
{
    readonly string root;

    public UsageScannerTests()
    {
        root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(root, "projects", "demo", "sub"));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    const string LineTemplate =
        """{"type":"assistant","timestamp":"@TIME","requestId":"@REQ","message":{"id":"@ID","model":"@MODEL","usage":{"input_tokens":@INPUT,"output_tokens":@OUTPUT,"cache_creation_input_tokens":@WRITE1H,"cache_read_input_tokens":@READ,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":@WRITE1H},"speed":"@SPEED"}}}""";

    static string Line(string id, string req, string time, string model = "claude-opus-5-5",
                       long input = 0, long output = 0, long write1h = 0, long read = 0, string speed = "standard") =>
        LineTemplate
            .Replace("@TIME", time).Replace("@REQ", req).Replace("@ID", id).Replace("@MODEL", model)
            .Replace("@INPUT", input.ToString()).Replace("@OUTPUT", output.ToString())
            .Replace("@WRITE1H", write1h.ToString()).Replace("@READ", read.ToString()).Replace("@SPEED", speed);

    /// <summary>16 Nov 2026, 08:00–08:00 UTC.</summary>
    static UsagePeriod Period() =>
        UsagePeriod.Current(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero), resetHour: 8, TimeZoneInfo.Utc);

    string ProjectFile(params string[] parts) => Path.Combine([root, "projects", .. parts]);

    [Fact]
    public void SummarizesDedupesAndFiltersByPeriod()
    {
        string[] lines =
        [
            """{"type":"user","timestamp":"2026-11-16T09:00:00.000Z","message":{"role":"user","content":"hi"}}""",
            // Streaming duplicates of one response: only the final one (most output) counts.
            Line("msg_1", "req_1", "2026-11-16T09:00:01.000Z", input: 1_000_000, output: 10),
            Line("msg_1", "req_1", "2026-11-16T09:00:02.000Z", input: 1_000_000, output: 1_000_000),
            // Before the period: ignored.
            Line("msg_0", "req_0", "2026-11-16T07:59:59.000Z", input: 5_000_000),
            "not json",
        ];
        File.WriteAllText(ProjectFile("demo", "a.jsonl"), string.Join("\n", lines));
        // Subagent logs live in nested folders.
        File.WriteAllText(ProjectFile("demo", "sub", "b.jsonl"),
            Line("msg_2", "req_2", "2026-11-16T10:00:00Z", model: "claude-sonnet-5-5", write1h: 1_000_000, read: 1_000_000));

        // Test files are freshly written, so modification time is "now" (after period start).
        var summary = new UsageScanner([root]).Summarize(Period());

        Assert.Equal(2, summary.Requests);
        Assert.Equal(1_000_000, summary.Tokens.Input);
        Assert.Equal(1_000_000, summary.Tokens.Output);
        Assert.Equal(1_000_000, summary.Tokens.CacheWrite1h);
        // Opus 5.5: $4 + $20; Sonnet 5.5: $4 (1h write) + $0.10 (read)
        Assert.Equal(28.10, summary.Cost, 6);
        Assert.Equal("claude-opus-5-5", summary.ByModel[0].Model);
        Assert.Equal(50, summary.PercentUsed(56.20), 6);
    }

    [Fact]
    public void DefaultRootsHonourClaudeConfigDir()
    {
        var roots = UsageScanner.DefaultRoots(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = @"C:\a, D:\b" });
        Assert.Equal(new[] { @"C:\a", @"D:\b" }, roots);
    }

    [Fact]
    public void DefaultRootsWithoutConfigDirUseHomeFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = UsageScanner.DefaultRoots(new Dictionary<string, string?>());
        Assert.Equal(new[] { Path.Combine(home, ".claude"), Path.Combine(home, ".config", "claude") }, roots);
    }

    [Fact]
    public void ExpandsTildeAndEnvironmentVariables()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".claude"), UsageScanner.ExpandPath(@"~\.claude"));
        Assert.Equal(Path.Combine(home, ".claude"), UsageScanner.ExpandPath("~/.claude"));
        Assert.Equal(home, UsageScanner.ExpandPath("~"));
        Assert.Equal(
            Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "x"),
            UsageScanner.ExpandPath(@"%TEMP%\x"));
    }

    [Fact]
    public void ReadsFileThatIsOpenForWriting()
    {
        // Claude Code keeps the transcript open while it appends to it.
        var path = ProjectFile("demo", "open.jsonl");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                Line("msg_3", "req_3", "2026-11-16T11:00:00Z", input: 1_000_000) + "\n");
            writer.Write(bytes);
            writer.Flush();

            var summary = new UsageScanner([root]).Summarize(Period());
            Assert.Equal(1, summary.Requests);
            Assert.Equal(4, summary.Cost, 6);
        }
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        File.WriteAllText(ProjectFile("demo", "crlf.jsonl"), string.Join("\r\n",
            Line("msg_4", "req_4", "2026-11-16T11:00:00Z", output: 1_000_000),
            Line("msg_5", "req_5", "2026-11-16T11:00:01Z", output: 1_000_000)) + "\r\n");

        var summary = new UsageScanner([root]).Summarize(Period());
        Assert.Equal(2, summary.Requests);
        Assert.Equal(40, summary.Cost, 6);
    }

    [Fact]
    public void SkipsFilesNotModifiedSincePeriodStart()
    {
        var path = ProjectFile("demo", "old.jsonl");
        File.WriteAllText(path, Line("msg_6", "req_6", "2026-11-16T11:00:00Z", input: 1_000_000));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, new UsageScanner([root]).Summarize(Period()).Requests);
    }

    [Fact]
    public void UncategorisedCacheWritesBillAtFiveMinuteRate()
    {
        // cache_creation_input_tokens without a 5m/1h split, plus fast mode.
        var line = """{"timestamp":"2026-11-16T11:00:00Z","message":{"id":"m","model":"claude-opus-5-5","usage":{"cache_creation_input_tokens":1000000,"speed":"fast"}}}""";
        var entry = UsageScanner.ParseLine(System.Text.Encoding.UTF8.GetBytes(line));
        Assert.NotNull(entry);
        Assert.Equal(1_000_000, entry.Value.Usage.CacheWrite5m);
        Assert.True(entry.Value.FastMode);
        Assert.Equal("m", entry.Value.DedupeKey);
    }
}
