using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Rectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageTray;

/// <summary>
/// The floating widget: a small always-on-top pill showing "$12.34 · 25%", like the macOS menu bar.
/// Drag it anywhere (the position is remembered); click it to open the panel.
/// It never takes focus, stays out of Alt+Tab, and hides while a full-screen app is in front.
/// </summary>
public partial class PillWindow : Window
{
    readonly UsageStore store;
    readonly AppSettings settings;
    readonly WinForms.ContextMenuStrip menu;
    readonly Action<Rectangle> onClick;
    readonly DispatcherTimer fullScreenTimer;
    IntPtr hwnd;
    bool enabled;
    bool placed;
    bool hiddenForFullScreen;
    Native.RECT lastRect;

    internal PillWindow(UsageStore store, AppSettings settings, WinForms.ContextMenuStrip menu, Action<Rectangle> onClick)
    {
        this.store = store;
        this.settings = settings;
        this.menu = menu;
        this.onClick = onClick;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExtendedStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, remove: Native.WS_EX_APPWINDOW);
        };
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += (_, e) =>
        {
            menu.Show(WinForms.Cursor.Position);
            e.Handled = true;
        };
        SizeChanged += (_, e) => KeepInPlace(e.NewSize);

        store.Changed += (_, _) => Render();
        settings.Changed += (_, _) => Render();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) EnsureOnScreen();
        });

        fullScreenTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => UpdateVisibility(), Dispatcher);
        Render();
    }

    /// <summary>Shows or hides the widget (the "Show floating widget" setting).</summary>
    internal void SetEnabled(bool value)
    {
        enabled = value;
        if (enabled) fullScreenTimer.Start(); else fullScreenTimer.Stop();
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        hiddenForFullScreen = enabled && Native.IsFullScreenAppInFront();
        var show = enabled && !hiddenForFullScreen;
        if (show && !IsVisible)
        {
            Show();
            if (!placed) PlaceInitially();
        }
        else if (!show && IsVisible)
        {
            Hide();
        }
    }

    internal Rectangle ScreenRect => Native.GetWindowRect(hwnd, out var r)
        ? new Rectangle(r.Left, r.Top, r.Width, r.Height)
        : Rectangle.Empty;

    void Render()
    {
        if (store.Summary is not { } summary)
        {
            LabelText.Text = "Claude …";
            Dot.Visibility = Visibility.Collapsed;
            return;
        }
        var percent = summary.PercentUsed(settings.DailyBudget);
        LabelText.Text = Format.Label(summary.Cost, percent, settings.PillShows);
        Dot.Visibility = Visibility.Visible;
        Dot.SetResourceReference(Shape.FillProperty, Palette.ForPercent(percent));
    }

    // Position

    /// <summary>The saved position if it's still on a screen, otherwise just above the tray.</summary>
    void PlaceInitially()
    {
        placed = true;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var rect);
        if (settings.PillX is { } x && settings.PillY is { } y
            && IsOnScreen(new Rectangle(x, y, rect.Width, rect.Height)))
        {
            Native.MoveWindow(hwnd, x, y);
        }
        else
        {
            var workArea = WinForms.Screen.PrimaryScreen?.WorkingArea ?? WinForms.SystemInformation.WorkingArea;
            var margin = (int)Math.Round(6 * Native.GetDpiForWindow(hwnd) / 96.0);
            Native.MoveWindow(hwnd, workArea.Right - rect.Width - margin, workArea.Bottom - rect.Height);
        }
        Native.GetWindowRect(hwnd, out lastRect);
    }

    static bool IsOnScreen(Rectangle rect) =>
        WinForms.Screen.AllScreens.Any(s =>
        {
            var visible = Rectangle.Intersect(s.WorkingArea, rect);
            return visible.Width >= rect.Width / 2 && visible.Height >= rect.Height / 2;
        });

    /// <summary>
    /// When the text changes width, keep the edge nearest the screen side still (so a widget in the
    /// bottom-right corner grows to the left), and keep it on screen.
    /// </summary>
    void KeepInPlace(Size newSize)
    {
        if (!placed || hwnd == IntPtr.Zero || !IsVisible) return;
        // SizeChanged can arrive before the window itself has been resized, so work out the new
        // width from the layout size rather than asking Windows for it.
        var newWidth = (int)Math.Round(newSize.Width * Native.GetDpiForWindow(hwnd) / 96.0);
        if (lastRect.Width > 0 && newWidth != lastRect.Width)
        {
            var screen = WinForms.Screen.FromRectangle(ToRectangle(lastRect)).WorkingArea;
            var anchoredRight = lastRect.Left + lastRect.Width / 2 > screen.Left + screen.Width / 2;
            if (anchoredRight) Native.MoveWindow(hwnd, lastRect.Right - newWidth, lastRect.Top);
        }
        // Once the resize has landed, make sure it's still on screen and remember where it is.
        Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Background);
    }

    static Rectangle ToRectangle(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    void EnsureOnScreen()
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var rect);
        if (rect.Width <= 0) return;
        var area = WinForms.Screen.FromRectangle(ToRectangle(rect)).WorkingArea;
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - rect.Width));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - rect.Height));
        if (x != rect.Left || y != rect.Top) Native.MoveWindow(hwnd, x, y);
        Native.GetWindowRect(hwnd, out lastRect);
    }

    void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Native.GetWindowRect(hwnd, out var before);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already released.
        }
        Native.GetWindowRect(hwnd, out var after);
        var moved = Math.Abs(after.Left - before.Left) + Math.Abs(after.Top - before.Top) > 3;
        if (moved)
        {
            EnsureOnScreen();
            Native.GetWindowRect(hwnd, out var final);
            settings.Update(s =>
            {
                s.PillX = final.Left;
                s.PillY = final.Top;
            });
        }
        else
        {
            onClick(ScreenRect);
        }
        e.Handled = true;
    }

    /// <summary>Lays the widget out off-screen for <c>--snapshot</c>.</summary>
    internal void ShowForSnapshot()
    {
        Render();
        if (!IsVisible) Show();
    }
}
