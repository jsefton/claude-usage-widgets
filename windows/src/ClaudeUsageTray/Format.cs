using System.Globalization;

namespace ClaudeUsageTray;

/// <summary>Text formatting shared by the tray icon, floating widget and panel.</summary>
static class Format
{
    static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>Costs are always US dollars (API list prices): "$12.34".</summary>
    public static string Cost(double value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>An amount in the account's billing currency, e.g. "£40.00" or "US$500.00".</summary>
    public static string Money(double value, string currency, int decimalPlaces = 2)
    {
        var format = (NumberFormatInfo)Culture.NumberFormat.Clone();
        format.CurrencySymbol = CurrencySymbol(currency);
        format.CurrencyDecimalDigits = Math.Clamp(decimalPlaces, 0, 6);
        return value.ToString("C", format);
    }

    static string CurrencySymbol(string code)
    {
        code = code.ToUpperInvariant();
        try
        {
            if (new RegionInfo(Culture.Name).ISOCurrencySymbol == code) return Culture.NumberFormat.CurrencySymbol;
        }
        catch (ArgumentException)
        {
            // Neutral or invariant culture: no region.
        }
        return code switch
        {
            "USD" => "US$",
            "GBP" => "£",
            "EUR" => "€",
            "JPY" => "¥",
            "CAD" => "CA$",
            "AUD" => "A$",
            "NZD" => "NZ$",
            "INR" => "₹",
            _ => code + " ",
        };
    }

    /// <summary>"20:30" for times in the next 24 hours, otherwise "Wed 18 Nov".</summary>
    public static string Reset(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return date - DateTimeOffset.Now < TimeSpan.FromHours(24)
            ? local.ToString("t", Culture)
            : local.ToString("ddd d MMM", Culture);
    }

    /// <summary>"Mon 1 Dec".</summary>
    public static string Day(DateTime date) => date.ToString("ddd d MMM", Culture);

    /// <summary>"1st", "2nd", "23rd".</summary>
    public static string Ordinal(int value)
    {
        var suffix = (value % 100) is 11 or 12 or 13 ? "th" : (value % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    public static string Time(DateTimeOffset date) => date.ToLocalTime().ToString("t", Culture);

    /// <summary>"16 Nov, 08:00".</summary>
    public static string DayAndTime(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return $"{local.ToString("d MMM", Culture)}, {local.ToString("t", Culture)}";
    }

    public static string Percent(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1e9).ToString("0.00", Culture) + "B",
        >= 1_000_000 => (value / 1e6).ToString("0.0", Culture) + "M",
        >= 1_000 => (value / 1e3).ToString("0.0", Culture) + "K",
        _ => value.ToString(Culture),
    };

    public static string Hour(int hour) => DateTime.Today.AddHours(hour).ToString("t", Culture);

    /// <summary>The floating widget's text, e.g. "$12.34 · 25%" (like the macOS menu bar).</summary>
    public static string Label(double cost, double percent, DisplayMode mode) => mode switch
    {
        DisplayMode.Cost => Cost(cost),
        DisplayMode.Percent => Percent(percent),
        _ => $"{Cost(cost)} · {Percent(percent)}",
    };
}
