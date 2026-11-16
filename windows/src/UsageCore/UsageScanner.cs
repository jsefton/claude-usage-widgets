using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageCore;

/// <summary>Totals for one model within a period.</summary>
public sealed class ModelSummary(string model)
{
    public string Model { get; } = model;
    public TokenUsage Tokens { get; internal set; }
    public double Cost { get; internal set; }
    public int Requests { get; internal set; }
}

/// <summary>Aggregated usage for a period.</summary>
public sealed class UsageSummary(UsagePeriod period)
{
    public UsagePeriod Period { get; } = period;
    public TokenUsage Tokens { get; internal set; }
    public double Cost { get; internal set; }
    public int Requests { get; internal set; }

    /// <summary>Responses from models with no known price (counted in tokens, not in cost).</summary>
    public int UnpricedRequests { get; internal set; }

    public IReadOnlyList<ModelSummary> ByModel { get; internal set; } = [];

    public double PercentUsed(double budget) => budget > 0 ? Cost / budget * 100 : 0;
}

/// <summary>
/// Reads Claude Code's local transcript logs (<c>&lt;config dir&gt;\projects\**\*.jsonl</c>) and totals the
/// token usage recorded on assistant messages.
/// </summary>
public sealed class UsageScanner(IReadOnlyList<string> roots)
{
    public IReadOnlyList<string> Roots { get; } = roots;

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Default Claude Code data directories: <c>CLAUDE_CONFIG_DIR</c> (comma separated) if set,
    /// otherwise <c>~\.claude</c> and <c>~\.config\claude</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var custom = environment is null
            ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            : environment.GetValueOrDefault("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return ParseRoots(custom);
        return [Path.Combine(Home, ".claude"), Path.Combine(Home, ".config", "claude")];
    }

    /// <summary>Splits a comma-separated list of folders, expanding <c>~</c> and <c>%VARIABLES%</c>.</summary>
    public static IReadOnlyList<string> ParseRoots(string commaSeparated) =>
        commaSeparated
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ExpandPath)
            .ToList();

    public static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path == "~") return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Home, path[2..]);
        }
        return path;
    }

    public UsageSummary Summarize(UsagePeriod period)
    {
        var entries = new Dictionary<string, Entry>();
        var anonymous = new List<Entry>();

        foreach (var file in LogFiles(period.Start))
        {
            byte[] data;
            int length;
            try
            {
                (data, length) = ReadShared(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var remaining = data.AsSpan(0, length);
            while (!remaining.IsEmpty)
            {
                var newline = remaining.IndexOf((byte)'\n');
                var line = newline < 0 ? remaining : remaining[..newline];
                remaining = newline < 0 ? Span<byte>.Empty : remaining[(newline + 1)..];

                if (ParseLine(line) is not { } entry || !period.Contains(entry.Timestamp)) continue;
                if (entry.DedupeKey is { } key)
                {
                    // Claude Code writes the same response several times while streaming;
                    // keep the copy with the most output tokens (the final one).
                    if (entries.TryGetValue(key, out var existing) && existing.Usage.Output >= entry.Usage.Output) continue;
                    entries[key] = entry;
                }
                else
                {
                    anonymous.Add(entry);
                }
            }
        }

        var summary = new UsageSummary(period);
        var models = new Dictionary<string, ModelSummary>();
        foreach (var entry in entries.Values.Concat(anonymous))
        {
            summary.Tokens += entry.Usage;
            summary.Requests += 1;
            if (!models.TryGetValue(entry.Model, out var model))
            {
                model = new ModelSummary(entry.Model);
                models[entry.Model] = model;
            }
            model.Tokens += entry.Usage;
            model.Requests += 1;
            if (Pricing.Cost(entry.Model, entry.Usage, entry.FastMode) is { } cost)
            {
                summary.Cost += cost;
                model.Cost += cost;
            }
            else
            {
                summary.UnpricedRequests += 1;
            }
        }
        summary.ByModel = models.Values
            .Where(m => m.Tokens.Total > 0)
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.Tokens.Total)
            .ToList();
        return summary;
    }

    // Files

    internal IEnumerable<string> LogFiles(DateTimeOffset cutoff)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var root in Roots)
        {
            var projects = new DirectoryInfo(Path.Combine(root, "projects"));
            if (!projects.Exists) continue;
            foreach (var file in projects.EnumerateFiles("*.jsonl", options))
            {
                if (!file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                // A file untouched since the period began can't contain entries from this period.
                if (file.LastWriteTimeUtc < cutoff.UtcDateTime) continue;
                if (seen.Add(ResolvedPath(file))) yield return file.FullName;
            }
        }
    }

    static string ResolvedPath(FileInfo file)
    {
        try
        {
            if (file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return target.FullName;
            }
        }
        catch (IOException)
        {
        }
        return file.FullName;
    }

    /// <summary>
    /// Reads a whole file while Claude Code may still have it open for writing (or be replacing it).
    /// </summary>
    internal static (byte[] Buffer, int Length) ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        using var memory = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(memory);
        return (memory.GetBuffer(), (int)memory.Length);
    }

    // Parsing

    internal readonly record struct Entry(
        DateTimeOffset Timestamp, string Model, TokenUsage Usage, bool FastMode, string? DedupeKey);

    static ReadOnlySpan<byte> UsageKey => "\"usage\""u8;

    internal static Entry? ParseLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        // Cheap pre-filter: most lines (user turns, tool results) carry no usage block.
        if (line.Length <= 20 || line.IndexOf(UsageKey) < 0) return null;

        Line? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize(line, LineJsonContext.Default.Line);
        }
        catch (JsonException)
        {
            return null;
        }
        if (decoded?.Message is not { Usage: { } usage } message || decoded.Timestamp is not { } stamp
            || ParseDate(stamp) is not { } timestamp)
        {
            return null;
        }

        var created = usage.CacheCreationInputTokens ?? 0;
        long write5m, write1h;
        if (usage.CacheCreation is { } split)
        {
            write5m = split.Ephemeral5m ?? 0;
            write1h = split.Ephemeral1h ?? 0;
            // Anything not attributed to a TTL is billed at the default 5-minute rate.
            write5m += Math.Max(0, created - write5m - write1h);
        }
        else
        {
            write5m = created;
            write1h = 0;
        }
        var tokens = new TokenUsage(
            Input: usage.InputTokens ?? 0,
            Output: usage.OutputTokens ?? 0,
            CacheWrite5m: write5m,
            CacheWrite1h: write1h,
            CacheRead: usage.CacheReadInputTokens ?? 0);

        var key = (message.Id, decoded.RequestId) switch
        {
            ({ } id, { } request) => $"{id}:{request}",
            ({ } id, null) => id,
            _ => null,
        };
        return new Entry(timestamp, message.Model ?? "unknown", tokens, usage.Speed == "fast", key);
    }

    internal static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    // Wire format: only the fields we need; everything optional.

    internal sealed class Line
    {
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("requestId")] public string? RequestId { get; set; }
        [JsonPropertyName("message")] public LineMessage? Message { get; set; }
    }

    internal sealed class LineMessage
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("usage")] public LineUsage? Usage { get; set; }
    }

    internal sealed class LineUsage
    {
        [JsonPropertyName("input_tokens")] public long? InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public long? OutputTokens { get; set; }
        [JsonPropertyName("cache_creation_input_tokens")] public long? CacheCreationInputTokens { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; set; }
        [JsonPropertyName("cache_creation")] public LineCacheCreation? CacheCreation { get; set; }
        [JsonPropertyName("speed")] public string? Speed { get; set; }
    }

    internal sealed class LineCacheCreation
    {
        [JsonPropertyName("ephemeral_5m_input_tokens")] public long? Ephemeral5m { get; set; }
        [JsonPropertyName("ephemeral_1h_input_tokens")] public long? Ephemeral1h { get; set; }
    }
}

[JsonSerializable(typeof(UsageScanner.Line))]
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
internal sealed partial class LineJsonContext : JsonSerializerContext;
