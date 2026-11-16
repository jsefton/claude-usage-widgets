using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageTray;

/// <summary>
/// A tray-only app: no main window. Starts the store, tray icon, floating widget and panel.
/// </summary>
public partial class App : Application
{
    const string InstanceName = @"Local\ClaudeUsageTray";
    const string ShowRequestName = @"Local\ClaudeUsageTray.ShowPanel";

    Mutex? instance;
    EventWaitHandle? showRequest;
    RegisteredWaitHandle? showRequestWait;

    AppSettings settings = null!;
    UsageStore store = null!;
    FlyoutWindow flyout = null!;
    PillWindow pill = null!;
    TrayIcon? tray;
    WinForms.ContextMenuStrip menu = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var snapshotDirectory = Snapshot.DirectoryFromArgs(e.Args);

        if (snapshotDirectory is null && !ClaimSingleInstance())
        {
            Shutdown();
            return;
        }

        Palette.Apply(Resources, Palette.SystemUsesDarkTheme());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        WinForms.Application.EnableVisualStyles();

        settings = AppSettings.Load();
        store = new UsageStore(settings);
        menu = BuildMenu();
        flyout = new FlyoutWindow(store, settings, Quit);
        pill = new PillWindow(store, settings, menu, anchor => flyout.Toggle(anchor));

        if (snapshotDirectory is not null)
        {
            await Snapshot.RunAsync(this, snapshotDirectory, store, flyout, pill);
            Shutdown();
            return;
        }

        tray = new TrayIcon(store, settings, menu, anchor => flyout.Toggle(anchor));
        settings.Changed += (_, _) => pill.SetEnabled(settings.ShowPill);
        pill.SetEnabled(settings.ShowPill);
    }

    /// <summary>
    /// Returns false if another copy is already running, after asking it to open its panel
    /// (so launching the app again from the Start menu shows the panel).
    /// </summary>
    bool ClaimSingleInstance()
    {
        instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var request = EventWaitHandle.OpenExisting(ShowRequestName);
                // This copy was just launched by the user, so it may take the foreground; let the
                // running copy do so, or Windows would keep its panel from getting focus.
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                request.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return false;
        }

        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        showRequestWait = ThreadPool.RegisterWaitForSingleObject(
            showRequest,
            (_, _) => Dispatcher.BeginInvoke(() => flyout?.ShowAt(DefaultAnchor())),
            null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    WinForms.ContextMenuStrip BuildMenu()
    {
        var strip = new WinForms.ContextMenuStrip();
        var open = new WinForms.ToolStripMenuItem("Open", null, (_, _) => flyout.ShowAt(CursorAnchor()));
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        strip.Items.Add(open);
        strip.Items.Add("Refresh now", null, (_, _) => store.Refresh(force: true));
        var showPill = new WinForms.ToolStripMenuItem("Show floating widget");
        showPill.Click += (_, _) => settings.Update(s => s.ShowPill = !s.ShowPill);
        strip.Items.Add(showPill);
        strip.Items.Add(new WinForms.ToolStripSeparator());
        strip.Items.Add("Quit", null, (_, _) => Quit());
        strip.Opening += (_, _) => showPill.Checked = settings.ShowPill;
        return strip;
    }

    static Rectangle CursorAnchor() => new(WinForms.Cursor.Position, new System.Drawing.Size(1, 1));

    /// <summary>Next to the floating widget if it's showing, otherwise the bottom-right corner (the tray).</summary>
    Rectangle DefaultAnchor()
    {
        if (pill.IsVisible && pill.ScreenRect is { IsEmpty: false } rect) return rect;
        var area = WinForms.Screen.PrimaryScreen?.WorkingArea ?? WinForms.SystemInformation.WorkingArea;
        return new Rectangle(area.Right - 1, area.Bottom, 1, 1);
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark switches arrive as General (or Color) changes, on a SystemEvents thread.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Dispatcher.BeginInvoke(() => Palette.Apply(Resources, Palette.SystemUsesDarkTheme()));
        }
    }

    void Quit()
    {
        tray?.Dispose();
        tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Remove the tray icon now rather than leaving a ghost until the mouse passes over it.
        tray?.Dispose();
        store?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        showRequestWait?.Unregister(null);
        showRequest?.Dispose();
        if (instance is not null)
        {
            try
            {
                instance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            instance.Dispose();
        }
        base.OnExit(e);
    }
}
