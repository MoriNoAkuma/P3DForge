using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace P3DForge.App.Viewport;

/// <summary>
/// A pool of TextBlocks on a canvas, re-positioned every frame (Begin / Add… / End). Elements are reused and
/// collapsed instead of being recreated, so following the camera costs only position updates.
/// Labels are decluttered: <see cref="Add"/> skips a label whose (estimated) box overlaps one already placed in
/// this frame, so dense proxy / memory-point sets stay readable; add the important labels first.
/// </summary>
public sealed class LabelLayer(Canvas canvas)
{
    static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0xA8, 0x14, 0x16, 0x1B)));
    public static readonly Brush ProxyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xD4, 0xF2)));
    public static readonly Brush MemoryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xDC, 0x8C)));

    const double CharWidth = 6.1, PadX = 7, LineHeight = 16;

    readonly List<TextBlock> pool = [];
    readonly List<Rect> placed = [];
    int used;

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public int Count => used;

    /// <summary>Labels skipped in this frame because they would overlap a placed one.</summary>
    public int Skipped { get; private set; }

    public void Begin()
    {
        used = 0;
        Skipped = 0;
        placed.Clear();
    }

    /// <summary>Places a label with its top-left corner at (x, y) unless it would overlap an earlier one (or <paramref name="force"/>).</summary>
    public bool Add(double x, double y, string text, Brush foreground, bool force = false)
    {
        var box = new Rect(Math.Round(x), Math.Round(y), text.Length * CharWidth + PadX, LineHeight);
        if (!force)
            foreach (var r in placed)
                if (r.IntersectsWith(box))
                {
                    Skipped++;
                    return false;
                }
        placed.Add(box);
        TextBlock tb;
        if (used < pool.Count) tb = pool[used];
        else
        {
            tb = new TextBlock { FontSize = 11, Background = Back, Padding = new Thickness(3, 0, 3, 1), IsHitTestVisible = false };
            pool.Add(tb);
            canvas.Children.Add(tb);
        }
        if (!string.Equals(tb.Text, text, StringComparison.Ordinal)) tb.Text = text;
        if (!ReferenceEquals(tb.Foreground, foreground)) tb.Foreground = foreground;
        Canvas.SetLeft(tb, box.X);
        Canvas.SetTop(tb, box.Y);
        if (tb.Visibility != Visibility.Visible) tb.Visibility = Visibility.Visible;
        used++;
        return true;
    }

    public void End()
    {
        for (int i = used; i < pool.Count; i++)
            if (pool[i].Visibility != Visibility.Collapsed) pool[i].Visibility = Visibility.Collapsed;
    }
}
