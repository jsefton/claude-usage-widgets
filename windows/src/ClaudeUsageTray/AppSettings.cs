using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray;

/// <summary>What the floating widget shows.</summary>
public enum DisplayMode { CostAndPercent, Cost, Percent }

/// <summary>What the number on the tray icon is.</summary>
public enum TrayBadge { Percent, Dollars }

/// <summary>
/// User settings, stored as JSON in <c>%APPDATA%\ClaudeUsage\settings.json</c>.
/// Change them through <see cref="Update"/>, which saves and raises <see cref="Changed"/>.
/// </summary>
public sealed class AppSettings
{
    public const double DefaultDailyBudget = 25;
    public const int DefaultResetHour = 8;
    public const int DefaultMonthlyResetDay = 1;

    public double DailyBudget { get; set; } = DefaultDailyBudget;
    public int ResetHour { get; set; } = DefaultResetHour;
    public DisplayMode PillShows { get; set; } = DisplayMode.CostAndPercent;
    public TrayBadge TrayShows { get; set; } = TrayBadge.Percent;
    public bool ShowPill { get; set; } = true;
    /// <summary>The floating widget's top-left corner in screen pixels, once it has been moved.</summary>
    public int? PillX { get; set; }
    public int? PillY { get; set; }
    /// <summary>Comma-separated Claude config folders; empty means the defaults.</summary>
    public string CustomDataDir { get; set; } = "";
    public bool ShowPlanUsage { get; set; } = true;
    /// <summary>Day of the month an Enterprise monthly spend limit resets (1–31).</summary>
    public int MonthlyResetDay { get; set; } = DefaultMonthlyResetDay;

    public event EventHandler? Changed;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsage", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }
        settings.Sanitize();
        return settings;
    }

    public void Update(Action<AppSettings> change)
    {
        change(this);
        Sanitize();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Sanitize()
    {
        if (!(DailyBudget > 0) || double.IsInfinity(DailyBudget)) DailyBudget = DefaultDailyBudget;
        ResetHour = Math.Clamp(ResetHour, 0, 23);
        MonthlyResetDay = Math.Clamp(MonthlyResetDay, 1, 31);
        if (!Enum.IsDefined(PillShows)) PillShows = DisplayMode.CostAndPercent;
        if (!Enum.IsDefined(TrayShows)) TrayShows = TrayBadge.Percent;
        CustomDataDir ??= "";
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings still apply for this session.
        }
    }
}
