using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using UsageCore;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageTray;

/// <summary>
/// The notification-area icon: a small square showing today's % (or dollars), coloured green,
/// orange or red, with the details in its tooltip. Left-click opens the panel.
/// </summary>
sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon notifyIcon;
    readonly UsageStore store;
    readonly AppSettings settings;
    Icon? icon;
    string? iconKey;

    public TrayIcon(UsageStore store, AppSettings settings, WinForms.ContextMenuStrip menu, Action<Rectangle> onClick)
    {
        this.store = store;
        this.settings = settings;
        notifyIcon = new WinForms.NotifyIcon { ContextMenuStrip = menu, Text = "Claude usage" };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) onClick(new Rectangle(WinForms.Cursor.Position, new Size(1, 1)));
        };
        store.Changed += OnChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Update();
        notifyIcon.Visible = true;
    }

    void OnChanged(object? sender, EventArgs e) => Update();

    void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            iconKey = null; // The small-icon size may have changed with the scaling.
            Update();
        });

    void Update()
    {
        var size = WinForms.SystemInformation.SmallIconSize.Width;
        var summary = store.Summary;
        string key;
        Func<Icon> make;
        if (summary is null)
        {
            key = $"app|{size}";
            make = () => LoadAppIcon(size);
        }
        else
        {
            var percent = summary.PercentUsed(settings.DailyBudget);
            var text = BadgeText(summary.Cost, percent, settings.TrayShows);
            var color = Palette.BadgeColor(percent);
            key = $"{text}|{color.ToArgb()}|{size}";
            make = () => Badge.CreateIcon(text, color, size);
        }
        if (key != iconKey)
        {
            var old = icon;
            icon = make();
            notifyIcon.Icon = icon;
            old?.Dispose();
            iconKey = key;
        }
        notifyIcon.Text = Tooltip(summary);
    }

    internal static string BadgeText(double cost, double percent, TrayBadge mode)
    {
        if (mode == TrayBadge.Dollars)
        {
            return cost switch
            {
                < 9.5 => "$" + Math.Round(cost).ToString("0"),
                < 999.5 => Math.Round(cost).ToString("0"),
                _ => Math.Min(Math.Round(cost / 1000), 99).ToString("0") + "K",
            };
        }
        return Math.Min(Math.Round(percent), 999).ToString("0");
    }

    string Tooltip(UsageSummary? summary)
    {
        if (summary is null) return "Claude usage";
        var percent = summary.PercentUsed(settings.DailyBudget);
        var lines = new List<string>
        {
            $"Claude today: {Format.Cost(summary.Cost)} · {Format.Percent(percent)}",
            $"of {Format.Cost(settings.DailyBudget)} budget · resets {Format.Time(summary.Period.End)}",
        };
        if (settings.ShowPlanUsage && store.Plan is { } plan && plan.Limits.Count > 0)
        {
            lines.Add(string.Join(" · ", plan.Limits.Take(2).Select(l => $"{l.Label} {Format.Percent(l.Percent)}")));
        }
        var text = string.Join("\n", lines);
        // NotifyIcon.Text is limited to 127 characters.
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    static Icon LoadAppIcon(int size)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/AppIcon.ico"));
        using var stream = resource?.Stream ?? throw new FileNotFoundException("AppIcon.ico resource is missing.");
        return new Icon(stream, size, size);
    }

    public void Dispose()
    {
        store.Changed -= OnChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon?.Dispose();
    }
}

/// <summary>Draws the tray badge: white text centred on a rounded, coloured square.</summary>
static class Badge
{
    public static Icon CreateIcon(string text, Color fill, int size)
    {
        using var bitmap = Render(text, fill, size);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            // GetHicon allocates a GDI handle that Icon.FromHandle doesn't own.
            Native.DestroyIcon(handle);
        }
    }

    public static Bitmap Render(string text, Color fill, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        using (var body = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, body);
        }

        // Lay the text out as a path so it can be centred exactly and scaled to fit.
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return bitmap;

        var maxWidth = size * (text.Length >= 3 ? 0.90f : 0.80f);
        var maxHeight = size * 0.62f;
        var scale = Math.Min(maxWidth / bounds.Width, maxHeight / bounds.Height);
        using var transform = new Matrix();
        transform.Translate(size / 2f, size / 2f);
        transform.Scale(scale, scale);
        transform.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
        path.Transform(transform);
        g.FillPath(Brushes.White, path);
        return bitmap;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
