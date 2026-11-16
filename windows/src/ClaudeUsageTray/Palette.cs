using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Theme-aware brushes for the panel and floating widget, kept in the application resources so
/// <c>{DynamicResource ...}</c> and <c>SetResourceReference</c> pick up light/dark switches live.
/// (Standard controls are styled by WPF's Fluent theme.)
/// </summary>
static class Palette
{
    public const string Background = "PanelBackground";
    public const string Border = "PanelBorder";
    public const string Text = "TextPrimary";
    public const string Secondary = "TextSecondary";
    public const string Track = "MeterTrack";
    public const string Hover = "HoverFill";
    public const string Good = "StatusGood";
    public const string Warning = "StatusWarning";
    public const string Critical = "StatusCritical";

    public static bool IsDark { get; private set; }

    public static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void Apply(ResourceDictionary resources, bool dark)
    {
        IsDark = dark;
        Set(resources, Background, dark ? 0x2C2C2C : 0xF9F9F9);
        Set(resources, Border, dark ? 0x454545 : 0xDCDCDC);
        Set(resources, Text, dark ? 0xFFFFFF : 0x1A1A1A);
        Set(resources, Secondary, dark ? 0xC2C2C2 : 0x5E5E5E);
        Set(resources, Track, dark ? 0x4A4A4A : 0xE1E1E1);
        Set(resources, Hover, dark ? 0x3A3A3A : 0xEDEDED);
        Set(resources, Good, dark ? 0x6CCB5F : 0x0F7B0F);
        Set(resources, Warning, dark ? 0xF7A23B : 0xB65A00);
        Set(resources, Critical, dark ? 0xFF8A8A : 0xC42B1C);
    }

    static void Set(ResourceDictionary resources, string key, int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>Green under 75%, orange under 100%, red from 100% (the same thresholds as the macOS app).</summary>
    public static string ForPercent(double percent) => percent switch
    {
        < 75 => Good,
        < 100 => Warning,
        _ => Critical,
    };

    /// <summary>Uses the server's severity when it gives one, otherwise the daily thresholds.</summary>
    public static string ForSeverity(string? severity, double percent) => severity switch
    {
        "critical" => Critical,
        "warning" => Warning,
        "normal" => Good,
        _ => ForPercent(percent),
    };

    /// <summary>Tray badge fill: fixed mid-tones that carry white text on light and dark taskbars.</summary>
    public static System.Drawing.Color BadgeColor(double percent) => percent switch
    {
        < 75 => System.Drawing.Color.FromArgb(0x10, 0x7C, 0x10),
        < 100 => System.Drawing.Color.FromArgb(0xC8, 0x5A, 0x00),
        _ => System.Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C),
    };
}
