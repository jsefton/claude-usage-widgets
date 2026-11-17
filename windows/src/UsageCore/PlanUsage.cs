using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UsageCore;

/// <summary>
/// Usage figures reported by Claude itself for the signed-in account: the monthly spend limit
/// (Enterprise spend limits, or usage credits on Pro/Max) and the plan's limit meters
/// (5-hour session, weekly, ...).
/// </summary>
/// <remarks>
/// Parsed from <c>GET https://api.anthropic.com/api/oauth/usage</c>, the endpoint behind Claude
/// Code's <c>/usage</c> screen. It is undocumented, so every field is optional and anything
/// unexpected (missing, or of the wrong type) is skipped rather than treated as an error.
/// </remarks>
public sealed record PlanUsage(MonthlySpend? Monthly, IReadOnlyList<PlanLimit> Limits)
{
    /// <summary>
    /// The login's plan from Claude Code's saved credentials, e.g. "max" or "enterprise".
    /// Not part of the usage response; set by whoever fetched it.
    /// </summary>
    public string? SubscriptionType { get; init; }

    /// <summary>Personal plans whose monthly figure is a usage-credits cap, not an organisation spend limit.</summary>
    static readonly HashSet<string> PersonalPlans = new(StringComparer.OrdinalIgnoreCase) { "free", "pro", "max" };

    /// <summary>
    /// True when the monthly figure is a spend limit set for the account (Enterprise and other
    /// organisation plans), so a reset date and forecast make sense.
    /// </summary>
    public bool HasMonthlySpendLimit =>
        Monthly is { Limit: > 0, IsEnabled: true }
        && (SubscriptionType is null || !PersonalPlans.Contains(SubscriptionType));

    /// <summary>The forecast for the monthly spend limit, when the account has one.</summary>
    /// <param name="now">The current local time.</param>
    public MonthlyForecast? Forecast(int resetDay, DateTime now) =>
        HasMonthlySpendLimit && Monthly is { Limit: { } limit } monthly
            ? MonthlyForecast.Make(monthly.Used, limit, resetDay, now)
            : null;

    public static PlanUsage? Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>Parses the usage endpoint's JSON body. Returns null only when the body isn't a JSON object.</summary>
    public static PlanUsage? Parse(ReadOnlyMemory<byte> data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var limits = new List<PlanLimit>();
            if (Child(root, "limits") is { ValueKind: JsonValueKind.Array } rows)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (Str(row, "kind") is not { } kind || Num(row, "percent") is not { } percent) continue;
                    var group = Str(row, "group") ?? kind;
                    limits.Add(new PlanLimit(
                        Kind: kind,
                        Group: group,
                        Label: Label(kind, group, Child(row, "scope")),
                        Percent: percent,
                        ResetsAt: ParseDate(Str(row, "resets_at")),
                        Severity: Str(row, "severity")));
                }
            }

            // A "monthly" meter, when present, carries the monthly period's reset date.
            var monthlyReset = limits.FirstOrDefault(l => l.Group == "monthly")?.ResetsAt;

            MonthlySpend? monthly = null;
            var spend = Child(root, "spend");
            var extra = Child(root, "extra_usage");
            if (spend is { } s && Child(s, "used") is { } used && Major(used) is { } usedMajor)
            {
                // Newer, unit-explicit shape.
                var limitMoney = Child(s, "limit");
                var limit = (limitMoney is { } l ? Major(l) : null)
                    ?? (Child(s, "cap") is { } cap && Child(cap, "money") is { } capMoney ? Major(capMoney) : null);
                monthly = new MonthlySpend(
                    Used: usedMajor,
                    Limit: limit,
                    Currency: Str(used, "currency") ?? (limitMoney is { } lm ? Str(lm, "currency") : null) ?? "USD",
                    DecimalPlaces: Int(used, "exponent") ?? 2,
                    Percent: Num(s, "percent") ?? (limit is > 0 ? usedMajor / limit.Value * 100 : null),
                    ResetsAt: monthlyReset,
                    IsEnabled: Bool(s, "enabled") ?? true,
                    DisabledReason: Str(s, "disabled_reason"),
                    Severity: Str(s, "severity"));
            }
            else if (extra is { } e && Num(e, "used_credits") is { } usedMinor)
            {
                // Older shape: amounts in minor units of `currency` (cents for USD).
                var places = Int(e, "decimal_places") ?? 2;
                var scale = Math.Pow(10, places);
                monthly = new MonthlySpend(
                    Used: usedMinor / scale,
                    Limit: Num(e, "monthly_limit") / scale,
                    Currency: Str(e, "currency")?.ToUpperInvariant() ?? "USD",
                    DecimalPlaces: places,
                    Percent: Num(e, "utilization"),
                    ResetsAt: monthlyReset,
                    IsEnabled: Bool(e, "is_enabled") ?? true,
                    DisabledReason: Str(e, "disabled_reason"));
            }

            return new PlanUsage(monthly, limits);
        }
    }

    internal static string Label(string kind, string group, JsonElement? scope)
    {
        string? scoped = null;
        if (scope is { ValueKind: JsonValueKind.Object } sc)
        {
            scoped = (Child(sc, "model") is { } model ? Str(model, "display_name") : null)
                ?? (Child(sc, "surface") is { } surface ? Str(surface, "display_name") : null);
        }
        var label = kind switch
        {
            "session" => "5-hour session",
            "weekly_all" or "weekly_scoped" => "Weekly",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(group.Replace('_', ' ')),
        };
        return string.IsNullOrEmpty(scoped) ? label : $"{label} · {scoped}";
    }

    /// <summary>Parses ISO 8601 timestamps, with or without fractional seconds (the server sends microseconds).</summary>
    public static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrEmpty(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    // Lenient JSON accessors: a missing property or one of an unexpected type reads as null.

    static JsonElement? Child(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    static string? Str(JsonElement obj, string name) =>
        Child(obj, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static double? Num(JsonElement obj, string name) =>
        Child(obj, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) ? number : null;

    static int? Int(JsonElement obj, string name) =>
        Num(obj, name) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    static bool? Bool(JsonElement obj, string name) => Child(obj, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    /// <summary>A money object (<c>amount_minor</c>, <c>exponent</c>) in major units, e.g. dollars.</summary>
    static double? Major(JsonElement money) =>
        Num(money, "amount_minor") is { } minor ? minor / Math.Pow(10, Int(money, "exponent") ?? 2) : null;
}

/// <summary>Money spent this billing period against a monthly cap.</summary>
/// <param name="Used">Amount used, in major units (e.g. dollars, pounds).</param>
/// <param name="Limit">The monthly cap in major units, or null when there is no cap.</param>
/// <param name="Currency">ISO 4217 code, e.g. "USD" or "GBP".</param>
/// <param name="DecimalPlaces">Decimal places the server uses for this currency.</param>
/// <param name="Percent">Share of the cap used, 0–100+, or null when there is no cap.</param>
/// <param name="ResetsAt">When the monthly period resets, if the server says.</param>
/// <param name="IsEnabled">False while this spend can't be used (e.g. out of credits).</param>
/// <param name="DisabledReason">The server's reason when not enabled, e.g. "out_of_credits".</param>
/// <param name="Severity">The server's reading for colouring: "normal", "warning" or "critical".</param>
public sealed record MonthlySpend(
    double Used,
    double? Limit,
    string Currency,
    int DecimalPlaces,
    double? Percent,
    DateTimeOffset? ResetsAt = null,
    bool IsEnabled = true,
    string? DisabledReason = null,
    string? Severity = null);

/// <summary>One of the plan's limit meters, e.g. the 5-hour session or the weekly limit.</summary>
/// <param name="Kind">The server's meter kind, e.g. "session", "weekly_all", "weekly_scoped".</param>
/// <param name="Group">The server's row group, e.g. "session", "weekly", "monthly".</param>
/// <param name="Label">Human-readable label built from the kind and scope.</param>
/// <param name="Percent">Share of the window used, 0–100.</param>
/// <param name="ResetsAt">When the window resets, if the server says.</param>
/// <param name="Severity">"normal", "warning" or "critical".</param>
public sealed record PlanLimit(
    string Kind, string Group, string Label, double Percent, DateTimeOffset? ResetsAt, string? Severity)
{
    public string Id => $"{Kind}|{Label}";
}
