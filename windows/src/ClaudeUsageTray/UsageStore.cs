using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using UsageCore;

namespace ClaudeUsageTray;

/// <summary>
/// Holds today's usage (from local logs) and the plan usage (from Claude), refreshing on a timer.
/// Raises <see cref="Changed"/> on the UI thread whenever anything shown on screen changes.
/// </summary>
sealed class UsageStore : IDisposable
{
    readonly AppSettings settings;
    readonly DispatcherTimer timer;

    public UsageSummary? Summary { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }
    public bool DataDirectoryFound { get; private set; } = true;

    /// <summary>Usage reported by Claude for the signed-in account (monthly spend, plan limits).</summary>
    public PlanUsage? Plan { get; private set; }
    public string? PlanError { get; private set; }
    public bool IsFetchingPlan { get; private set; }
    DateTimeOffset? planFetchedAt;

    /// <summary>How often to ask Claude for plan usage (Claude Code itself polls about every 5 minutes).</summary>
    static readonly TimeSpan PlanInterval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ForcedPlanInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    bool pendingRefresh;
    (int ResetHour, string DataDir, bool ShowPlan) applied;

    public event EventHandler? Changed;

    public UsageStore(AppSettings settings)
    {
        this.settings = settings;
        applied = (settings.ResetHour, settings.CustomDataDir, settings.ShowPlanUsage);
        settings.Changed += OnSettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        timer.Start();
        Refresh();
    }

    public IReadOnlyList<string> Roots =>
        string.IsNullOrWhiteSpace(settings.CustomDataDir)
            ? UsageScanner.DefaultRoots()
            : UsageScanner.ParseRoots(settings.CustomDataDir);

    /// <summary>
    /// Refreshes the local daily total, and the plan usage from Claude when it is due.
    /// </summary>
    /// <param name="force">Fetch plan usage now (still at most every 30 seconds), e.g. the refresh button.</param>
    public async void Refresh(bool force = false)
    {
        RefreshPlan(force);
        if (IsRefreshing)
        {
            pendingRefresh = true;
            return;
        }
        IsRefreshing = true;
        Changed?.Invoke(this, EventArgs.Empty);

        var period = UsagePeriod.Current(resetHour: settings.ResetHour);
        var roots = Roots;
        try
        {
            var (found, summary) = await Task.Run(() =>
            {
                var found = roots.Any(root => Directory.Exists(Path.Combine(root, "projects")));
                return (found, new UsageScanner(roots).Summarize(period));
            });
            Summary = summary;
            DataDirectoryFound = found;
            LastUpdated = DateTimeOffset.Now;
        }
        catch (Exception)
        {
            // Keep the last good figures; the next tick tries again.
        }
        IsRefreshing = false;
        Changed?.Invoke(this, EventArgs.Empty);

        if (pendingRefresh)
        {
            pendingRefresh = false;
            Refresh();
        }
    }

    public async void RefreshPlan(bool force = false)
    {
        if (!settings.ShowPlanUsage)
        {
            if (Plan is not null || PlanError is not null)
            {
                Plan = null;
                PlanError = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            planFetchedAt = null;
            return;
        }
        if (IsFetchingPlan) return;
        if (planFetchedAt is { } fetched && DateTimeOffset.Now - fetched < (force ? ForcedPlanInterval : PlanInterval)) return;

        IsFetchingPlan = true;
        try
        {
            Plan = await ClaudeAccount.FetchPlanUsageAsync(Roots);
            PlanError = null;
        }
        catch (ClaudeAccount.FetchException e)
        {
            PlanError = e.Message;
        }
        catch (Exception e)
        {
            PlanError = e.Message;
        }
        planFetchedAt = DateTimeOffset.Now;
        IsFetchingPlan = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void OnSettingsChanged(object? sender, EventArgs e)
    {
        var now = (settings.ResetHour, settings.CustomDataDir, settings.ShowPlanUsage);
        if (now.ShowPlanUsage != applied.ShowPlan) RefreshPlan(force: true);
        if (now.ResetHour != applied.ResetHour || now.CustomDataDir != applied.DataDir) Refresh();
        applied = now;
        // Budget and display changes only need a redraw.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // SystemEvents call back on their own thread.
        timer.Dispatcher.BeginInvoke(() => Refresh(force: true));
    }

    public void Dispose()
    {
        timer.Stop();
        settings.Changed -= OnSettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
