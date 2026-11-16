using System.Windows;
using System.Windows.Controls;

namespace ClaudeUsageTray;

/// <summary>A thin rounded bar showing a fraction, coloured from a <see cref="Palette"/> key.</summary>
sealed class Meter : Grid
{
    readonly ColumnDefinition filled = new();
    readonly ColumnDefinition empty = new();
    readonly Border fill;

    public Meter() : this(6) { }

    public Meter(double thickness)
    {
        Height = thickness;
        ColumnDefinitions.Add(filled);
        ColumnDefinitions.Add(empty);

        var track = new Border { CornerRadius = new CornerRadius(thickness / 2) };
        track.SetResourceReference(Border.BackgroundProperty, Palette.Track);
        SetColumnSpan(track, 2);
        Children.Add(track);

        fill = new Border { CornerRadius = new CornerRadius(thickness / 2) };
        Children.Add(fill);
        Set(0, Palette.Good);
    }

    public void Set(double fraction, string brushKey)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        filled.Width = new GridLength(fraction, GridUnitType.Star);
        empty.Width = new GridLength(1 - fraction, GridUnitType.Star);
        fill.SetResourceReference(Border.BackgroundProperty, brushKey);
        fill.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
