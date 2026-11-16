using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeUsageTray;

/// <summary>The settings shown at the bottom of the panel. Every change is saved immediately.</summary>
public partial class SettingsPanel : UserControl
{
    AppSettings settings = null!;
    /// <summary>True while copying settings into the controls, so that doesn't write them back.</summary>
    bool syncing;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    internal void Attach(AppSettings appSettings)
    {
        settings = appSettings;

        for (var hour = 0; hour < 24; hour++) ResetHourBox.Items.Add(Format.Hour(hour));
        TrayShowsBox.Items.Add("Percent used");
        TrayShowsBox.Items.Add("Dollars spent");
        PillShowsBox.Items.Add("Cost and %");
        PillShowsBox.Items.Add("Cost only");
        PillShowsBox.Items.Add("% only");

        Sync();
        settings.Changed += (_, _) => Sync();

        BudgetBox.LostKeyboardFocus += (_, _) => CommitBudget();
        BudgetBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitBudget();
        };
        DataDirBox.LostKeyboardFocus += (_, _) => CommitDataDir();
        DataDirBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitDataDir();
        };

        ResetHourBox.SelectionChanged += (_, _) => Change(s => s.ResetHour = Math.Max(ResetHourBox.SelectedIndex, 0));
        TrayShowsBox.SelectionChanged += (_, _) => Change(s => s.TrayShows = (TrayBadge)Math.Max(TrayShowsBox.SelectedIndex, 0));
        PillShowsBox.SelectionChanged += (_, _) => Change(s => s.PillShows = (DisplayMode)Math.Max(PillShowsBox.SelectedIndex, 0));
        // Checked/Unchecked rather than Click: Click isn't raised when assistive tech toggles a checkbox.
        OnToggled(ShowPillBox, on => Change(s => s.ShowPill = on));
        OnToggled(ShowPlanBox, on => Change(s => s.ShowPlanUsage = on));
        OnToggled(LaunchAtLoginBox, on =>
        {
            if (!syncing) SetLaunchAtLogin(on);
        });
    }

    static void OnToggled(CheckBox box, Action<bool> handler)
    {
        box.Checked += (_, _) => handler(true);
        box.Unchecked += (_, _) => handler(false);
    }

    void Sync()
    {
        syncing = true;
        try
        {
            if (!BudgetBox.IsKeyboardFocusWithin) BudgetBox.Text = settings.DailyBudget.ToString("0.##", CultureInfo.CurrentCulture);
            if (!DataDirBox.IsKeyboardFocusWithin) DataDirBox.Text = settings.CustomDataDir;
            ResetHourBox.SelectedIndex = settings.ResetHour;
            TrayShowsBox.SelectedIndex = (int)settings.TrayShows;
            PillShowsBox.SelectedIndex = (int)settings.PillShows;
            ShowPillBox.IsChecked = settings.ShowPill;
            ShowPlanBox.IsChecked = settings.ShowPlanUsage;
            LaunchAtLoginBox.IsChecked = StartupRegistration.IsEnabled;
        }
        finally
        {
            syncing = false;
        }
    }

    void Change(Action<AppSettings> change)
    {
        if (!syncing) settings.Update(change);
    }

    void CommitBudget()
    {
        var text = BudgetBox.Text.Trim().TrimStart('$');
        if ((double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var budget)
             || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out budget))
            && budget > 0 && double.IsFinite(budget))
        {
            budget = Math.Round(budget, 2);
            if (budget != settings.DailyBudget) Change(s => s.DailyBudget = budget);
        }
        BudgetBox.Text = settings.DailyBudget.ToString("0.##", CultureInfo.CurrentCulture);
    }

    void CommitDataDir()
    {
        var text = DataDirBox.Text.Trim();
        if (text != settings.CustomDataDir) Change(s => s.CustomDataDir = text);
    }

    void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            LoginError.Visibility = Visibility.Collapsed;
        }
        catch (Exception e)
        {
            LoginError.Text = $"Couldn't update the login item: {e.Message}";
            LoginError.Visibility = Visibility.Visible;
        }
        syncing = true;
        try
        {
            LaunchAtLoginBox.IsChecked = StartupRegistration.IsEnabled;
        }
        finally
        {
            syncing = false;
        }
    }
}
