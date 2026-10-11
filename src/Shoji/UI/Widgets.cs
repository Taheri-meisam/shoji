using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Shoji.UI;

/// <summary>Small helpers for building module layouts in code.</summary>
public static class W
{
    public static TextBlock Text(string text = "", double size = Theme.Small, Brush? color = null,
                                 FontWeight? weight = null, bool mono = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = color ?? Theme.Text,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = mono ? Theme.Mono : Theme.Font,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Large text in the theme's display font (clock digits, big stats), with the theme's glow/glitch effect.</summary>
    public static TextBlock Number(string text, double size, Brush? color = null, FontWeight? weight = null)
    {
        var t = Text(text, size, color, weight ?? Theme.Current.NumberWeight);
        t.FontFamily = Theme.Display;
        t.Effect = Theme.NumberEffect;
        return t;
    }

    /// <summary>A "label …… value" row.</summary>
    public static Grid Row(string label, out TextBlock value, Brush? labelColor = null) =>
        Row(label, out value, out _, labelColor);

    public static Grid Row(string label, out TextBlock value, out TextBlock labelText, Brush? labelColor = null)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelText = Text(label, Theme.Small, labelColor ?? Theme.Dim);
        value = Text("–", Theme.Small);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(value, 1);
        g.Children.Add(labelText);
        g.Children.Add(value);
        return g;
    }

    /// <summary>Lays children out in equal-width columns.</summary>
    public static UniformGrid Columns(params UIElement[] children)
    {
        var g = new UniformGrid { Rows = 1 };
        foreach (var c in children)
            g.Children.Add(c);
        return g;
    }

    /// <summary>Columns with a small gap between them (for side-by-side label/value rows).</summary>
    public static UniformGrid SpacedColumns(params FrameworkElement[] children)
    {
        var g = Columns(children);
        for (int i = 0; i < children.Length - 1; i++)
            children[i].Margin = new Thickness(0, 0, 10, 0);
        return g;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children)
            s.Children.Add(c);
        return s;
    }

    /// <summary>Big number with a small caption underneath.</summary>
    public static StackPanel Stat(string caption, out TextBlock value, double size = Theme.Big) =>
        Stat(caption, out value, out _, size);

    public static StackPanel Stat(string caption, out TextBlock value, out TextBlock captionText, double size = Theme.Big)
    {
        value = Number("–", size);
        value.Margin = new Thickness(0, -4, 0, -2);
        captionText = Text(caption, Theme.Small, Theme.Dim);
        return Stack(value, captionText);
    }

    public static string Format(double? value, string format, string unit) =>
        value.HasValue ? value.Value.ToString(format) + unit : "–";

    /// <summary>A °C reading in the configured unit, e.g. "61°" (or " °C" / " °F" with <paramref name="unit"/>).</summary>
    public static string Temp(double? celsius, bool unit = false)
    {
        if (celsius is not double c)
            return "–";
        double v = Theme.Fahrenheit ? c * 9 / 5 + 32 : c;
        return v.ToString("0") + (unit ? Theme.Fahrenheit ? " °F" : " °C" : "°");
    }

    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1000 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return bytes.ToString(bytes >= 100 || i == 0 ? "0" : "0.0") + " " + units[i];
    }

    public static string Rate(double bytesPerSecond)
    {
        // Network speeds read more naturally in bits (Mbps) like ISPs advertise.
        double bits = bytesPerSecond * 8;
        if (bits >= 1e9) return (bits / 1e9).ToString("0.00") + " Gbps";
        if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0") + " Mbps";
        if (bits >= 1e3) return (bits / 1e3).ToString("0") + " Kbps";
        return bits.ToString("0") + " bps";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}

/// <summary>A themed card with a title, hosting one module.</summary>
public sealed class Tile : Grid
{
    UIElement? _glow;
    Density _density = Density.Normal;

    /// <summary>How much room this tile has right now (see <see cref="Responsive"/>).</summary>
    public Density Density => _density;

    /// <summary>Stops the neon flicker (called when the tile is thrown away on a rebuild).</summary>
    public void StopAnimations()
    {
        Motion.Changed -= SyncFlicker;
        if (_glow != null)
            Animations.Stop(_glow);
        _flickering = false;
    }

    bool _flickering;

    /// <summary>Neon flicker runs unless motion is paused (screen off, PC locked, night mode black).</summary>
    void SyncFlicker()
    {
        if (_glow == null || _flickering == !Motion.Paused)
            return;
        _flickering = !Motion.Paused;
        if (_flickering)
            Animations.Flicker(_glow);
        else
            Animations.Stop(_glow);
    }

    public Tile(string title, string? kanji, UIElement content, UIElement? headerRight = null)
    {
        var t = Theme.Current;
        Margin = new Thickness(5);

        Children.Add(new TileChrome(outlineOnly: false)
        {
            Effect = t.SoftShadow
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.12 }
                : null,
        });
        // The neon glow is drawn by a separate outline-only layer, so changing tile content never re-renders the blur.
        if (t.TileGlow)
        {
            var glow = new TileChrome(outlineOnly: true)
            {
                Effect = new DropShadowEffect { Color = Theme.AccentColor, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 },
            };
            Children.Add(glow);
            _glow = glow;
            SyncFlicker();
            Motion.Changed += SyncFlicker; // until StopAnimations (tiles move between page grids, so not Unloaded)
        }

        // Title keeps its width; the optional right-hand text (e.g. CPU model) gets the rest and trims.
        // Without right-hand text nothing fills: the title sizes to its text, so the kanji sits right after it.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = headerRight != null };
        // A DockPanel (not a horizontal StackPanel) so a title too long for a narrow tile ends in "…".
        var titlePanel = new DockPanel { Margin = new Thickness(0, 0, 8, 0), LastChildFill = true };
        if (t.Title == TitleStyle.Kanji && !string.IsNullOrEmpty(kanji))
        {
            var k = W.Text(kanji, Theme.Title - 0.5, t.TileGlow ? Theme.Accent2 : Theme.Dim);
            k.FontFamily = Theme.TitleFont;
            k.Margin = new Thickness(8, 0, 0, 0);
            k.Opacity = 0.9;
            DockPanel.SetDock(k, Dock.Right);
            titlePanel.Children.Add(k.ShowFrom(Density.Normal));
        }
        titlePanel.Children.Add(TitleText(title, t));
        DockPanel.SetDock(titlePanel, Dock.Left);
        header.Children.Add(titlePanel);
        if (headerRight is FrameworkElement right)
        {
            right.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(right.ShowFrom(Density.Normal)); // CPU model, place name… make room for the title
        }

        var dock = new DockPanel { Margin = new Thickness(14, 10, 14, 12), ClipToBounds = true };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        Children.Add(dock);

        // Tiles show less when they're small (and more when roomy) instead of shrinking everything.
        SizeChanged += (_, e) =>
        {
            var d = Responsive.For(e.NewSize.Width, e.NewSize.Height);
            if (d == _density) return;
            _density = d;
            Responsive.Apply(this, d);
        };
        Loaded += (_, _) => Responsive.Apply(this, _density = Responsive.For(ActualWidth, ActualHeight));

        TileHold.Attach(this); // press and hold → the tile's edit sheet (see TileEditor)
    }

    static TextBlock TitleText(string title, ThemeDef t)
    {
        string text = t.UpperTitles ? title.ToUpperInvariant() : title;
        text = t.Title switch
        {
            TitleStyle.Arrow => "▸ " + text,
            TitleStyle.Slash => "// " + text,
            TitleStyle.Prompt => "> " + text.ToLowerInvariant().Replace(' ', '_') + "_",
            _ => text,
        };
        var tb = W.Text(text, Theme.Title, Theme.Accent, FontWeights.SemiBold);
        if (t.Title == TitleStyle.Kanji)
            tb.FontFamily = Theme.TitleFont;
        return tb;
    }
}

/// <summary>Draws a tile's background and frame in the theme's shape.</summary>
public sealed class TileChrome : FrameworkElement
{
    readonly bool _outlineOnly;

    public TileChrome(bool outlineOnly)
    {
        _outlineOnly = outlineOnly;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4)
            return;

        var t = Theme.Current;
        var fill = _outlineOnly ? null : Theme.Tile;
        var borderBrush = _outlineOnly ? Theme.Accent : Theme.TileBorder;
        var border = borderBrush == null ? null : new Pen(borderBrush, _outlineOnly ? 1.5 : t.BorderThickness);
        var r = new Rect(0.5, 0.5, w - 1, h - 1);

        switch (t.Shape)
        {
            case TileShape.Hud:
                dc.DrawRectangle(fill, border, r);
                if (!_outlineOnly)
                    DrawBrackets(dc, r);
                break;

            case TileShape.Chamfer:
                double c = Math.Min(16, Math.Min(w, h) / 5);
                var shape = Polygon(new Point(r.Left + c, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom - c),
                                    new Point(r.Right - c, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + c));
                dc.DrawGeometry(fill, border, shape);
                if (!_outlineOnly)
                {
                    var accent = new Pen(Theme.Accent, 2);
                    dc.DrawLine(accent, new Point(r.Left, r.Top + c), new Point(r.Left + c, r.Top));
                    dc.DrawLine(accent, new Point(r.Right, r.Bottom - c), new Point(r.Right - c, r.Bottom));
                    dc.DrawRectangle(Theme.Accent, null, new Rect(r.Left + c + 4, r.Top, Math.Min(64, w / 4), 3));
                    // Little "data port" ticks in the top-right corner.
                    for (int i = 0; i < 3; i++)
                        dc.DrawRectangle(Theme.Accent, null, new Rect(r.Right - 10 - i * 7, r.Top + 5, 4, 4));
                }
                break;

            case TileShape.Ink:
                dc.DrawRoundedRectangle(fill, border, r, t.Radius, t.Radius);
                if (!_outlineOnly)
                {
                    // A brush stroke along the top that fades out, like ink running dry.
                    var stroke = new LinearGradientBrush(Theme.AccentColor, Theme.WithAlpha(Theme.AccentColor, 0), 0);
                    stroke.Freeze();
                    dc.DrawRoundedRectangle(stroke, null, new Rect(r.Left + t.Radius, r.Top, w * 0.55, 2.5), 1.2, 1.2);
                }
                break;

            case TileShape.Blade:
                double big = Math.Min(t.Radius, Math.Min(w, h) / 3), small = Math.Min(4, big);
                dc.DrawGeometry(fill, border, Corners(r, big, small, big, small));
                if (!_outlineOnly)
                {
                    // Two slanted "racing" marks in the top-right corner.
                    for (int i = 0; i < 2; i++)
                    {
                        double x = r.Right - 26 - i * 9;
                        dc.DrawGeometry(Theme.Accent, null, Polygon(new Point(x, r.Top + 6), new Point(x + 5, r.Top + 6), new Point(x + 1, r.Top + 14), new Point(x - 4, r.Top + 14)));
                    }
                }
                break;

            case TileShape.Drafting:
                dc.DrawRectangle(fill, border, r);
                if (!_outlineOnly)
                {
                    // Registration marks just outside each corner, and a dimension line along the bottom.
                    var pen = new Pen(Theme.Accent2, 1);
                    double m = 6;
                    foreach (var (x, y, dx, dy) in new[] { (r.Left, r.Top, -1, -1), (r.Right, r.Top, 1, -1), (r.Left, r.Bottom, -1, 1), (r.Right, r.Bottom, 1, 1) })
                    {
                        dc.DrawLine(pen, new Point(x + dx * 2, y), new Point(x + dx * (2 + m), y));
                        dc.DrawLine(pen, new Point(x, y + dy * 2), new Point(x, y + dy * (2 + m)));
                    }
                    if (w > 120)
                    {
                        double y = r.Bottom - 6, x1 = r.Left + 14, x2 = r.Right - 14;
                        var thin = new Pen(new SolidColorBrush(Theme.WithAlpha(Theme.Accent2Color, 0x70)), 0.8);
                        dc.DrawLine(thin, new Point(x1, y), new Point(x2, y));
                        dc.DrawLine(thin, new Point(x1, y - 3), new Point(x1, y + 3));
                        dc.DrawLine(thin, new Point(x2, y - 3), new Point(x2, y + 3));
                    }
                }
                break;

            default:
                dc.DrawRoundedRectangle(fill, border, r, t.Radius, t.Radius);
                break;
        }
    }

    /// <summary>Rectangle with its own radius per corner (top-left, top-right, bottom-right, bottom-left).</summary>
    static Geometry Corners(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Top), true, false);
            ctx.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), true, false);
            ctx.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom), true, false);
            ctx.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left, r.Top + tl), true, false);
            ctx.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static void DrawBrackets(DrawingContext dc, Rect r)
    {
        var pen = new Pen(Theme.Accent, 2) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        double len = Math.Min(14, Math.Min(r.Width, r.Height) / 4);
        foreach (var (x, y, dx, dy) in new[]
                 {
                     (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1),
                     (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1),
                 })
        {
            dc.DrawLine(pen, new Point(x, y), new Point(x + dx * len, y));
            dc.DrawLine(pen, new Point(x, y), new Point(x, y + dy * len));
        }
        // Centre tick on the top edge.
        dc.DrawLine(new Pen(Theme.Accent, 1), new Point(r.Left + r.Width / 2 - 10, r.Top + 0.5), new Point(r.Left + r.Width / 2 + 10, r.Top + 0.5));
    }

    static Geometry Polygon(params Point[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Thin horizontal progress bar, drawn in the theme's meter style.</summary>
public sealed class Meter : FrameworkElement
{
    double _value;
    Brush _fill = Theme.Accent;

    public Meter(double height = 6)
    {
        Height = height;
        Margin = new Thickness(0, 3, 0, 3);
    }

    /// <summary>Sets the fill fraction (0–100) and optionally its color.</summary>
    public void Set(double? percent, Brush? fill = null)
    {
        _value = Math.Clamp(percent ?? 0, 0, 100);
        _fill = fill ?? Theme.Accent;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        switch (Theme.Current.Meter)
        {
            case MeterStyle.Segmented:
            {
                double cell = Math.Max(4, h * 0.9), gap = 2;
                int n = Math.Max(1, (int)((w + gap) / (cell + gap)));
                double cw = (w - gap * (n - 1)) / n;
                int lit = (int)Math.Round(n * _value / 100);
                for (int i = 0; i < n; i++)
                    dc.DrawRectangle(i < lit ? _fill : Theme.Track, null, new Rect(i * (cw + gap), 0, cw, h));
                break;
            }
            case MeterStyle.Line:
            {
                double track = Math.Max(1, h / 4);
                dc.DrawRectangle(Theme.Track, null, new Rect(0, (h - track) / 2, w, track));
                double fh = Math.Max(2, h * 0.6);
                dc.DrawRectangle(_fill, null, new Rect(0, (h - fh) / 2, w * _value / 100, fh));
                break;
            }
            default:
            {
                double r = h / 2;
                dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, w, h), r, r);
                double fw = w * _value / 100;
                if (fw > 0)
                    dc.DrawRoundedRectangle(_fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
                break;
            }
        }
    }
}

/// <summary>
/// Scrolling graph of one or two series from the shared <see cref="Sensors.HistoryStore"/> (e.g. download/upload).
/// The data lives outside the tile, so graphs stay filled when the theme or page changes.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    readonly string _seriesA;
    readonly string? _seriesB;
    readonly int _capacity;
    readonly Brush _strokeA, _strokeB;
    readonly Brush _fillA;
    readonly double? _fixedMax;

    /// <param name="fixedMax">Fixed top of the scale (100 for percentages); null = auto-scale.</param>
    public Sparkline(string seriesA, string? seriesB = null, double? fixedMax = 100, Brush? colorA = null, Brush? colorB = null, int capacity = 60)
    {
        _seriesA = seriesA;
        _seriesB = seriesB;
        _capacity = capacity;
        _fixedMax = fixedMax;
        _strokeA = colorA ?? Theme.Accent;
        _strokeB = colorB ?? Theme.Upload;

        var c = ((SolidColorBrush)_strokeA).Color;
        byte alpha = Theme.Current.GraphFill;
        _fillA = new LinearGradientBrush(Theme.WithAlpha(c, alpha), Theme.WithAlpha(c, 0), 90);
        _fillA.Freeze();
        MinHeight = 24;
        Margin = new Thickness(0, 6, 0, 0);
    }

    /// <summary>Redraw with the latest samples (call once per update).</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // Transparent background so the whole area is hit-testable and lines sit on a faint baseline.
        dc.DrawRectangle(Brushes.Transparent, new Pen(Theme.Track, 1), new Rect(0, h - 0.5, w, 0.5));

        var a = Sensors.HistoryStore.Shared.Recent(_seriesA, _capacity);
        var b = _seriesB == null ? Array.Empty<double>() : Sensors.HistoryStore.Shared.Recent(_seriesB, _capacity);
        double max = _fixedMax ?? Math.Max(1, Math.Max(MaxOf(a), MaxOf(b)) * 1.15);
        Graph.Draw(dc, a, _capacity, _strokeA, _fillA, max, w, h);
        Graph.Draw(dc, b, _capacity, _strokeB, null, max, w, h);
    }

    static double MaxOf(double[] v) => v.Length == 0 ? 0 : v.Max();
}

/// <summary>Line + area drawing shared by the small graphs and the History tile.</summary>
public static class Graph
{
    public static void Draw(DrawingContext dc, IReadOnlyList<double> data, int capacity, Brush stroke, Brush? fill, double max, double w, double h,
                            double min = 0)
    {
        if (data.Count < 2)
            return;

        double step = w / (Math.Max(capacity, data.Count) - 1);
        double Y(double v) => h - Math.Clamp((v - min) / Math.Max(max - min, 1e-9), 0, 1) * (h - 2) - 1;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            double x = w - (data.Count - 1) * step;
            ctx.BeginFigure(new Point(x, Y(data[0])), false, false);
            for (int i = 1; i < data.Count; i++)
                ctx.LineTo(new Point(x + i * step, Y(data[i])), true, true);
        }
        line.Freeze();

        if (fill != null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                double x = w - (data.Count - 1) * step;
                ctx.BeginFigure(new Point(x, h), true, true);
                for (int i = 0; i < data.Count; i++)
                    ctx.LineTo(new Point(x + i * step, Y(data[i])), false, false);
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        if (Theme.Current.GraphGlow && stroke is SolidColorBrush sc)
        {
            // Cheap neon: a wide translucent stroke under the real one.
            var glow = new Pen(new SolidColorBrush(Theme.WithAlpha(sc.Color, 60)), 6) { LineJoin = PenLineJoin.Round };
            glow.Freeze();
            dc.DrawGeometry(null, glow, line);
        }
        var pen = new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Finger-friendly horizontal slider (0–1) drawn in the theme's style.</summary>
public sealed class TouchSlider : FrameworkElement
{
    double _value;
    bool _dragging;

    public event Action<double>? Changed;

    /// <summary>Height of the drawn slider; the touch area around it is taller (40 DIP, a comfortable finger target).</summary>
    const double VisualHeight = 28;

    public TouchSlider()
    {
        Height = 40;
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    public bool IsDragging => _dragging;

    public double Value
    {
        get => _value;
        set
        {
            if (_dragging)
                return; // don't fight the finger with polled values
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public Brush? Fill { get; set; }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetFrom(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
            SetFrom(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e) => _dragging = false;

    void SetFrom(double x)
    {
        double knob = Math.Min(ActualHeight, VisualHeight) / 2;
        _value = Math.Clamp((x - knob) / Math.Max(1, ActualWidth - knob * 2), 0, 1);
        InvalidateVisual();
        Changed?.Invoke(_value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, full = ActualHeight, h = Math.Min(full, VisualHeight);
        if (w < h || h <= 0)
            return; // squeezed narrower than the knob (very narrow column): nothing sensible to draw
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, full)); // whole area is touchable
        dc.PushTransform(new TranslateTransform(0, (full - h) / 2)); // the slider itself is drawn centred, at its usual size
        double r = h / 2, track = Math.Max(4, h / 5), x = r + (w - 2 * r) * _value;
        bool square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        double cr = square ? 0 : track / 2;
        dc.DrawRoundedRectangle(Theme.Track, null, new Rect(r, (h - track) / 2, w - 2 * r, track), cr, cr);
        dc.DrawRoundedRectangle(Fill ?? Theme.Accent, null, new Rect(r, (h - track) / 2, Math.Max(0, x - r), track), cr, cr);
        if (square)
            dc.DrawRectangle(Fill ?? Theme.Accent, null, new Rect(x - r * 0.4, h * 0.15, r * 0.8, h * 0.7));
        else
            dc.DrawEllipse(Theme.Text, new Pen(Fill ?? Theme.Accent, 2), new Point(x, h / 2), r * 0.62, r * 0.62);
        dc.Pop();
    }
}

/// <summary>Tap handling with a little press feedback. The dashboard cancels taps that turn out to be page swipes.</summary>
public static class Tap
{
    public static T On<T>(T element, Action action) where T : UIElement
    {
        // Acts only when the press started on this element: a swipe or drag that ends on a "Sleep" button does nothing.
        bool pressed = false;
        element.MouseLeftButtonDown += (_, _) =>
        {
            pressed = true;
            element.Opacity = 0.6;
        };
        element.MouseLeave += (_, _) =>
        {
            pressed = false;
            element.Opacity = 1;
        };
        // Also when the dashboard swallowed the release because it was a swipe.
        element.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => element.Opacity = 1), handledEventsToo: true);
        element.MouseLeftButtonUp += (_, e) =>
        {
            element.Opacity = 1;
            if (!pressed)
                return;
            pressed = false;
            e.Handled = true;
            action();
        };
        if (element is FrameworkElement fe)
            fe.Cursor = System.Windows.Input.Cursors.Hand;
        return element;
    }
}

/// <summary>Icons: a name from DeckActions.Icons becomes a Segoe Fluent glyph; anything else is shown as text / emoji.</summary>
public static class Icon
{
    public static readonly FontFamily Fluent = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily Symbols = new("Segoe UI Emoji, Segoe UI Symbol");

    public static TextBlock Make(string? icon, double size, Brush? color = null)
    {
        var t = W.Text("", size, color);
        t.TextTrimming = TextTrimming.None;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        Set(t, icon);
        return t;
    }

    public static void Set(TextBlock t, string? icon)
    {
        icon = (icon ?? "").Trim();
        if (Core.DeckActions.Icons.TryGetValue(icon, out var glyph))
        {
            t.Text = glyph;
            t.FontFamily = Fluent;
        }
        else
        {
            t.Text = icon;
            t.FontFamily = icon.Length <= 2 && icon.Any(c => char.IsSurrogate(c) || c > 0x2000) ? Symbols : Theme.Font;
        }
    }
}

/// <summary>Tiny vertical bars, one per CPU thread.</summary>
public sealed class CoreBars : FrameworkElement
{
    IReadOnlyList<double> _loads = Array.Empty<double>();

    public CoreBars()
    {
        Height = 22;
        Margin = new Thickness(0, 6, 0, 0);
    }

    public void Set(IReadOnlyList<double> loads)
    {
        _loads = loads;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _loads.Count;
        if (n == 0)
            return;
        bool segmented = Theme.Current.Meter == MeterStyle.Segmented;
        double gap = n > 24 ? 1 : 2;
        double bw = Math.Max((ActualWidth - gap * (n - 1)) / n, 1);
        double h = ActualHeight;
        for (int i = 0; i < n; i++)
        {
            double x = i * (bw + gap);
            double load = Math.Clamp(_loads[i], 0, 100);
            var fill = Theme.ForPercent(load);
            if (segmented)
            {
                // LED-style stack of cells.
                const double cell = 3, cgap = 1;
                int cells = (int)((h + cgap) / (cell + cgap));
                int lit = (int)Math.Ceiling(cells * load / 100);
                for (int c = 0; c < cells; c++)
                    dc.DrawRectangle(c < lit ? fill : Theme.Track, null, new Rect(x, h - (c + 1) * (cell + cgap) + cgap, bw, cell));
                continue;
            }
            dc.DrawRectangle(Theme.Track, null, new Rect(x, 0, bw, h));
            double fh = h * load / 100;
            if (fh > 0)
                dc.DrawRectangle(fill, null, new Rect(x, h - fh, bw, fh));
        }
    }
}
