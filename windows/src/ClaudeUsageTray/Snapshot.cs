using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeUsageTray;

/// <summary>
/// <c>ClaudeUsage.exe --snapshot &lt;folder&gt;</c>: renders the panel, floating widget and tray badges
/// (light and dark) to PNG files and exits. Used for README screenshots and checking the layout;
/// nothing is shown on screen and no tray icon is added.
/// </summary>
static class Snapshot
{
    public static string? DirectoryFromArgs(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        return index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.GetFullPath("snapshots");
    }

    public static async Task RunAsync(App app, string directory, UsageStore store, FlyoutWindow flyout, PillWindow pill)
    {
        Directory.CreateDirectory(directory);

        // Wait for the first scan and the plan fetch (or give up after 20 s and render what we have).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((store.Summary is null || store.IsRefreshing || store.IsFetchingPlan) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
            Palette.Apply(app.Resources, dark);

            flyout.ShowForSnapshot(expandSettings: false);
            await Idle();
            Save(flyout, Path.Combine(directory, $"panel-{theme}.png"));

            flyout.ShowForSnapshot(expandSettings: true);
            await Idle();
            Save(flyout, Path.Combine(directory, $"panel-settings-{theme}.png"));

            pill.ShowForSnapshot();
            await Idle();
            Save(pill, Path.Combine(directory, $"widget-{theme}.png"));
        }
        SaveBadges(Path.Combine(directory, "tray-badges.png"));
    }

    /// <summary>Lets layout, theme changes and the Expander's open/close animation finish.</summary>
    static async Task Idle()
    {
        await Task.Delay(800);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Renders an element at 2x (like a 200% display) to a PNG.</summary>
    static void Save(FrameworkElement element, string path, double scale = 2)
    {
        element.UpdateLayout();
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>A sheet of tray badges at 16, 24 and 32 px, magnified 4x, on light and dark taskbar colours.</summary>
    static void SaveBadges(string path)
    {
        (string Text, double Percent)[] samples = [("8", 8), ("25", 25), ("80", 80), ("100", 100), ("250", 250), ("$12", 48)];
        int[] sizes = [16, 24, 32];
        const int zoom = 4, pad = 8;
        var cell = 32 * zoom + pad;
        using var sheet = new System.Drawing.Bitmap(samples.Length * cell + pad, sizes.Length * 2 * cell + pad);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.Clear(System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3));
        using (var darkBand = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x20, 0x20, 0x20)))
        {
            g.FillRectangle(darkBand, 0, sizes.Length * cell, sheet.Width, sizes.Length * cell + pad);
        }
        for (var band = 0; band < 2; band++)
        {
            for (var row = 0; row < sizes.Length; row++)
            {
                for (var col = 0; col < samples.Length; col++)
                {
                    using var badge = Badge.Render(samples[col].Text, Palette.BadgeColor(samples[col].Percent), sizes[row]);
                    var size = sizes[row] * zoom;
                    g.DrawImage(badge, pad + col * cell, pad + (band * sizes.Length + row) * cell, size, size);
                }
            }
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
