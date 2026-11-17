using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using UsageCore;
using Rectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageTray;

/// <summary>
/// The panel that opens from the tray icon or floating widget: today's cost against the budget,
/// the Claude plan's usage, token counts, cost per model, and the settings.
/// </summary>
public partial class FlyoutWindow : Window
{
    readonly UsageStore store;
    readonly AppSettings settings;
    IntPtr hwnd;
    /// <summary>What the panel opens next to (screen pixels): the clicked tray icon or widget.</summary>
    Rectangle anchor;
    DateTime lastHidden = DateTime.MinValue;
    bool positioning = true;
    bool repositioning;

    internal FlyoutWindow(UsageStore store, AppSettings settings, Action quit)
    {
        this.store = store;
        this.settings = settings;
        InitializeComponent();
        SettingsView.Attach(settings);

        store.Changed += (_, _) => Render();
        SettingsToggle.Click += (_, _) => ShowSettings(SettingsView.Visibility != Visibility.Visible);
        RefreshButton.Click += (_, _) => store.Refresh(force: true);
        QuitButton.Click += (_, _) => quit();
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
        // Grow and shrink away from the taskbar (or the widget) when the content changes height.
        SizeChanged += (_, e) =>
        {
            // SizeChanged can arrive before the window itself has been resized: place it for the
            // new layout size now, then check again once the resize has landed.
            Reposition(e.NewSize);
            Dispatcher.BeginInvoke(() => Reposition(), System.Windows.Threading.DispatcherPriority.Background);
        };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.UseRoundedCorners(hwnd);
            Native.AddExtendedStyle(hwnd, Native.WS_EX_TOOLWINDOW);
        };
        DpiChanged += (_, _) => Reposition();
        Render();
    }

    /// <summary>Opens the panel next to <paramref name="anchorPixels"/>, or closes it if it is open.</summary>
    internal void Toggle(Rectangle anchorPixels)
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }
        // A click on the tray icon first deactivates (and so hides) the panel; don't reopen it straight away.
        if (DateTime.UtcNow - lastHidden < TimeSpan.FromMilliseconds(300)) return;
        ShowAt(anchorPixels);
    }

    internal void ShowAt(Rectangle anchorPixels)
    {
        anchor = anchorPixels;
        positioning = true;
        ShowActivated = true;
        Render();
        if (!IsVisible) Show();
        Reposition();
        Activate();
        store.Refresh();
    }

    internal void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        lastHidden = DateTime.UtcNow;
    }

    /// <summary>Lays the panel out off-screen for <c>--snapshot</c>, without taking focus.</summary>
    internal void ShowForSnapshot(bool expandSettings)
    {
        positioning = false;
        ShowActivated = false;
        ShowSettings(expandSettings);
        Render();
        if (!IsVisible) Show();
    }

    void ShowSettings(bool show)
    {
        SettingsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SettingsChevron.Text = show ? "" : ""; // ChevronUp / ChevronDown
    }

    /// <param name="layoutSize">The new size in device-independent units, when it is known before the window has resized.</param>
    void Reposition(Size? layoutSize = null)
    {
        if (!positioning || repositioning || !IsVisible || hwnd == IntPtr.Zero) return;
        repositioning = true;
        try
        {
            var workArea = WinForms.Screen.FromRectangle(anchor).WorkingArea;
            var scale = Native.GetDpiForWindow(hwnd) / 96.0;
            var margin = (int)Math.Round(12 * scale);

            // Never taller than the screen: the content scrolls instead.
            var maxHeight = (workArea.Height - 2 * margin) / scale;
            if (Math.Abs(MaxHeight - maxHeight) > 0.5) MaxHeight = maxHeight;

            Native.GetWindowRect(hwnd, out var rect);
            var width = layoutSize is { } size ? (int)Math.Round(size.Width * scale) : rect.Width;
            var height = layoutSize is { } size2 ? (int)Math.Round(Math.Min(size2.Height, maxHeight) * scale) : rect.Height;
            var (x, y) = Place(anchor, width, height, workArea, margin);
            if (x != rect.Left || y != rect.Top) Native.MoveWindow(hwnd, x, y);
        }
        finally
        {
            repositioning = false;
        }
    }

    /// <summary>
    /// Where to put a <paramref name="width"/> x <paramref name="height"/> panel next to <paramref name="anchor"/>:
    /// above it when there's room (taskbar at the bottom, or a widget), otherwise below it, centred on it
    /// horizontally, and always inside the work area. All values are screen pixels.
    /// </summary>
    internal static (int X, int Y) Place(Rectangle anchor, int width, int height, Rectangle workArea, int margin)
    {
        var gap = margin / 2;
        var x = anchor.Left + anchor.Width / 2 - width / 2;
        x = Math.Clamp(x, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - margin - width));

        int y;
        if (anchor.Top - gap - height >= workArea.Top + margin)
        {
            y = Math.Min(anchor.Top - gap, workArea.Bottom - margin) - height;
        }
        else
        {
            y = Math.Max(anchor.Bottom + gap, workArea.Top + margin);
        }
        y = Math.Clamp(y, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - margin - height));
        return (x, y);
    }

    // Rendering

    void Render()
    {
        var summary = store.Summary;
        PeriodText.Text = summary is null
            ? ""
            : $"Since {Format.DayAndTime(summary.Period.Start)} · resets {Format.Time(summary.Period.End)}";
        NoLogsText.Visibility = store.DataDirectoryFound ? Visibility.Collapsed : Visibility.Visible;
        LoadingText.Visibility = summary is null ? Visibility.Visible : Visibility.Collapsed;
        UsageContent.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;

        if (summary is not null)
        {
            var budget = settings.DailyBudget;
            var percent = summary.PercentUsed(budget);
            var color = Palette.ForPercent(percent);
            CostText.Text = Format.Cost(summary.Cost);
            PercentText.Text = Format.Percent(percent);
            PercentText.SetResourceReference(TextBlock.ForegroundProperty, color);
            BudgetMeter.Set(percent / 100, color);
            BudgetText.Text = $"of {Format.Cost(budget)} daily budget";

            PlanSection.Visibility = settings.ShowPlanUsage ? Visibility.Visible : Visibility.Collapsed;
            if (settings.ShowPlanUsage) RenderPlan();
            RenderTokens(summary);
            RenderModels(summary);
        }

        UpdatedText.Text = store.LastUpdated is { } updated ? $"Updated {Format.Time(updated)}" : "";
        RefreshButton.IsEnabled = !store.IsRefreshing;
    }

    void RenderPlan()
    {
        PlanRows.Children.Clear();
        string? status = null;
        var statusColor = Palette.Secondary;

        if (store.Plan is { } plan)
        {
            if (plan.Monthly is { } monthly)
            {
                var forecast = plan.Forecast(settings.MonthlyResetDay, DateTime.Now);
                PlanRows.Children.Add(MonthlyRow(monthly, forecast));
                if (forecast is not null && monthly.Limit is not null) PlanRows.Children.Add(ForecastRows(forecast, monthly));
            }
            foreach (var limit in plan.Limits)
            {
                PlanRows.Children.Add(MeterRow(
                    limit.Label,
                    Format.Percent(limit.Percent),
                    limit.Percent,
                    limit.Severity,
                    limit.ResetsAt is { } resets ? $"Resets {Format.Reset(resets)}" : null));
            }
            if (plan.Monthly is null && plan.Limits.Count == 0) status = "No plan limits reported for this account.";
        }
        else if (store.PlanError is null)
        {
            status = "Loading…";
        }
        if (store.PlanError is { } error)
        {
            status = "⚠ " + error;
            statusColor = Palette.Warning;
        }

        PlanStatus.Text = status ?? "";
        PlanStatus.SetResourceReference(TextBlock.ForegroundProperty, statusColor);
        PlanStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        // The divider below supplies the spacing after the last row.
        if (status is null && PlanRows.Children.Count > 0 && PlanRows.Children[^1] is FrameworkElement last)
        {
            last.Margin = new Thickness(0);
        }
    }

    FrameworkElement MonthlyRow(MonthlySpend monthly, MonthlyForecast? forecast)
    {
        var used = Format.Money(monthly.Used, monthly.Currency, monthly.DecimalPlaces);
        var value = monthly.Limit is { } limit
            ? $"{used} of {Format.Money(limit, monthly.Currency, monthly.DecimalPlaces)}"
            : used;
        var notes = new List<string>();
        if (forecast is not null) notes.Add($"Resets {Format.Day(forecast.ResetsAt)}");
        else if (monthly.ResetsAt is { } resets) notes.Add($"Resets {Format.Reset(resets)}");
        if (!monthly.IsEnabled) notes.Add(monthly.DisabledReason == "out_of_credits" ? "Off: out of credits" : "Off");
        return MeterRow("Monthly spend", value, monthly.Percent, monthly.Severity,
                        notes.Count == 0 ? null : string.Join(" · ", notes), showsPercent: true);
    }

    /// <summary>Enterprise spend limits: whether the current pace stays under the limit, and what's left per working day.</summary>
    FrameworkElement ForecastRows(MonthlyForecast forecast, MonthlySpend monthly)
    {
        string Money(double value) => Format.Money(value, monthly.Currency, monthly.DecimalPlaces);

        var projected = Money(forecast.Projected);
        var (outlook, outlookColor) = forecast.Status switch
        {
            ForecastStatus.OnTrack => ($"On track · about {projected}", Palette.Good),
            ForecastStatus.Close => ($"Close to limit · about {projected}", Palette.Warning),
            _ => ($"Over by about {Money(forecast.ProjectedOver)}", Palette.Critical),
        };
        var days = forecast.WorkingDaysLeft;
        var perDay = forecast.PerWorkingDay is { } amount
            ? $"{Money(amount)} · {days} working day{(days == 1 ? "" : "s")} left"
            : "No working days left";

        var grid = new Grid
        {
            Margin = new Thickness(0, -4, 0, 10),
            ToolTip = $"Forecast: spend so far, plus the average per working day (Mon–Fri) so far for each working day "
                      + $"left until {Format.Day(forecast.ResetsAt)}. Per working day: the {Money(forecast.Remaining)} left, "
                      + "spread over the working days from today until the reset.",
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(grid, "Forecast", outlook, outlookColor);
        AddRow(grid, "Per working day", perDay, null);
        return grid;

        void AddRow(Grid target, string label, string value, string? valueColor)
        {
            var index = target.RowDefinitions.Count;
            target.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = (Style)FindResource("Caption");
            var labelText = new TextBlock { Text = label, Style = caption, Margin = new Thickness(0, 0, 12, 2) };
            var valueText = new TextBlock { Text = value, Style = caption, Margin = new Thickness(0, 0, 0, 2) };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, valueColor ?? Palette.Text);
            Grid.SetRow(labelText, index);
            Grid.SetRow(valueText, index);
            Grid.SetColumn(valueText, 1);
            target.Children.Add(labelText);
            target.Children.Add(valueText);
        }
    }

    FrameworkElement MeterRow(string title, string value, double? percent, string? severity, string? footnote,
                              bool showsPercent = false)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var header = new DockPanel();
        if (percent is { } shown && showsPercent)
        {
            var percentText = new TextBlock { Text = Format.Percent(shown), Margin = new Thickness(8, 0, 0, 0) };
            percentText.SetResourceReference(TextBlock.ForegroundProperty, Palette.ForSeverity(severity, shown));
            DockPanel.SetDock(percentText, Dock.Right);
            header.Children.Add(percentText);
        }
        var valueText = new TextBlock { Text = value, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(valueText, Dock.Right);
        header.Children.Add(valueText);
        header.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(header);

        if (percent is { } p)
        {
            var meter = new Meter(4) { Margin = new Thickness(0, 5, 0, 0) };
            meter.Set(Math.Max(p, 0) / 100, Palette.ForSeverity(severity, p));
            row.Children.Add(meter);
        }
        if (footnote is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = footnote,
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        return row;
    }

    void RenderTokens(UsageSummary summary)
    {
        TokenGrid.Children.Clear();
        TokenGrid.RowDefinitions.Clear();
        var rows = new List<(string, string)>
        {
            ("Requests", summary.Requests.ToString("N0")),
            ("Input", Format.Tokens(summary.Tokens.Input)),
            ("Output", Format.Tokens(summary.Tokens.Output)),
            ("Cache write", Format.Tokens(summary.Tokens.CacheWrite5m + summary.Tokens.CacheWrite1h)),
            ("Cache read", Format.Tokens(summary.Tokens.CacheRead)),
        };
        if (summary.UnpricedRequests > 0) rows.Add(("Unpriced", $"{summary.UnpricedRequests} responses"));

        foreach (var (label, value) in rows)
        {
            var index = TokenGrid.RowDefinitions.Count;
            TokenGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labelText = new TextBlock { Text = label, Margin = new Thickness(0, 0, 16, 3) };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, Palette.Secondary);
            var valueText = new TextBlock { Text = value, Margin = new Thickness(0, 0, 0, 3) };
            Grid.SetRow(labelText, index);
            Grid.SetRow(valueText, index);
            Grid.SetColumn(valueText, 1);
            TokenGrid.Children.Add(labelText);
            TokenGrid.Children.Add(valueText);
        }
    }

    void RenderModels(UsageSummary summary)
    {
        ModelRows.Children.Clear();
        ModelSection.Visibility = summary.ByModel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var model in summary.ByModel.Take(6))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var cost = new TextBlock { Text = Format.Cost(model.Cost), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(cost, Dock.Right);
            row.Children.Add(cost);
            row.Children.Add(new TextBlock { Text = model.Model, TextTrimming = TextTrimming.CharacterEllipsis });
            ModelRows.Children.Add(row);
        }
    }
}
