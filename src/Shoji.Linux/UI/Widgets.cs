using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shoji.UI;

/// <summary>Small helpers for building tile layouts in code (same names as the Windows version).</summary>
public static partial class W
{
    public static TextBlock Text(string text = "", double size = UI.Theme.Small, IBrush? color = null, FontWeight? weight = null, bool mono = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? UI.Theme.Text,
        FontWeight = weight ?? FontWeight.Normal,
        FontFamily = mono ? UI.Theme.Mono : UI.Theme.Font,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Large text in the theme's display font, with the theme's glow / glitch.</summary>
    public static TextBlock Number(string text, double size, IBrush? color = null, FontWeight? weight = null)
    {
        var t = Text(text, size, color, weight ?? UI.Theme.Current.NumberWeight.ToAvalonia());
        t.FontFamily = UI.Theme.Display;
        t.Effect = UI.Theme.NumberEffect;
        return t;
    }

    /// <summary>A "label …… value" row (the label is translated).</summary>
    public static Grid Row(string label, out TextBlock value, IBrush? labelColor = null) => Row(label, out value, out _, labelColor);

    public static Grid Row(string label, out TextBlock value, out TextBlock labelText, IBrush? labelColor = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
        labelText = Text(Core.L.T(label), UI.Theme.Small, labelColor ?? UI.Theme.Dim);
        value = Text("–", UI.Theme.Small);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(value, 1);
        g.Children.Add(labelText);
        g.Children.Add(value);
        return g;
    }

    public static UniformGrid Columns(params Control[] children)
    {
        var g = new UniformGrid { Rows = 1 };
        g.Children.AddRange(children);
        return g;
    }

    public static UniformGrid SpacedColumns(params Control[] children)
    {
        for (int i = 0; i < children.Length - 1; i++)
            children[i].Margin = new Thickness(0, 0, 10, 0);
        return Columns(children);
    }

    public static StackPanel Stack(params Control[] children)
    {
        var s = new StackPanel();
        s.Children.AddRange(children);
        return s;
    }

    public static StackPanel Stat(string caption, out TextBlock value, double size = UI.Theme.Big) => Stat(caption, out value, out _, size);

    public static StackPanel Stat(string caption, out TextBlock value, out TextBlock captionText, double size = UI.Theme.Big)
    {
        value = Number("–", size);
        value.Margin = new Thickness(0, -4, 0, -2);
        captionText = Text(Core.L.T(caption), UI.Theme.Small, UI.Theme.Dim);
        return Stack(value, captionText);
    }

    public static DockPanel Dock(Control fill, params (Control Child, Avalonia.Controls.Dock Side)[] docked)
    {
        var d = new DockPanel { LastChildFill = true };
        foreach (var (child, side) in docked)
        {
            DockPanel.SetDock(child, side);
            d.Children.Add(child);
        }
        d.Children.Add(fill);
        return d;
    }
}

/// <summary>A themed card with a title, hosting one tile's content.</summary>
public sealed class Tile : Grid
{
    Density _density = Density.Normal;
    readonly TileChrome? _glow;
    Avalonia.Threading.DispatcherTimer? _flicker;

    public Density Density => _density;

    public Tile(string title, string? kanji, Control content, Control? headerRight = null)
    {
        var t = UI.Theme.Current;
        Margin = new Thickness(5);
        var chrome = new TileChrome(outlineOnly: false);
        // Shadows and glows are drawn as box shadows rather than blur effects: an effect is re-blurred every time
        // anything near it repaints (20 times a second behind an animated backdrop), a box shadow is cheap.
        if (t.SoftShadow)
            chrome.Shadows = new BoxShadows(new BoxShadow { Color = Color.FromArgb(0x1F, 0, 0, 0), Blur = 16, OffsetY = 3 });
        Children.Add(chrome);
        // Neon outline on its own layer, so it can flicker without repainting the tile.
        if (t.TileGlow)
        {
            var glow = UI.Theme.AccentColor;
            _glow = new TileChrome(outlineOnly: true)
            {
                Shadows = new BoxShadows(new BoxShadow { Color = UI.Theme.WithAlpha(glow, 0xD0), Blur = 14 },
                    new[] { new BoxShadow { Color = UI.Theme.WithAlpha(glow, 0x50), Blur = 10, IsInset = true } }),
            };
            Children.Add(_glow);
            if (UI.Theme.Animate)
                StartFlicker();
        }

        // Title keeps its width; the optional right-hand text (e.g. CPU model) gets the rest and trims.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        // A DockPanel (not a horizontal StackPanel) so a title too long for a narrow tile ends in "…".
        // Left-aligned: with no right-hand text it is the header's last (filling) child, and the kanji stays by the title.
        var titlePanel = new DockPanel { Margin = new Thickness(0, 0, 8, 0), LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Left };
        if (t.Title == TitleStyle.Kanji && !string.IsNullOrEmpty(kanji))
        {
            var k = W.Text(kanji, UI.Theme.Title - 0.5, t.TileGlow ? UI.Theme.Accent2 : UI.Theme.Dim);
            k.FontFamily = UI.Theme.TitleFont;
            k.Margin = new Thickness(8, 0, 0, 0);
            k.Opacity = 0.9;
            k.TextTrimming = TextTrimming.None;
            DockPanel.SetDock(k, Avalonia.Controls.Dock.Right);
            titlePanel.Children.Add(k.ShowFrom(Density.Normal));
        }
        titlePanel.Children.Add(TitleText(title, t));
        DockPanel.SetDock(titlePanel, Avalonia.Controls.Dock.Left);
        header.Children.Add(titlePanel);
        if (headerRight != null)
        {
            headerRight.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(headerRight.ShowFrom(Density.Normal));
        }

        var dock = new DockPanel { Margin = new Thickness(14, 10, 14, 12), ClipToBounds = true };
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        Children.Add(dock);

        // Tiles show less when small and more when roomy, instead of shrinking everything.
        SizeChanged += (_, e) =>
        {
            var d = Responsive.For(e.NewSize.Width, e.NewSize.Height);
            if (d == _density)
                return;
            _density = d;
            Responsive.Apply(this, d);
        };
        AttachedToVisualTree += (_, _) => Responsive.Apply(this, _density = Responsive.For(Bounds.Width, Bounds.Height));
        DetachedFromVisualTree += (_, _) => _flicker?.Stop();
        // Re-laying out the pages (a resize) detaches and re-attaches every tile; keep the neon going.
        AttachedToVisualTree += (_, _) => { if (!_disposed) _flicker?.Start(); };
        // Hold the tile to edit it on the dashboard (order, width, style, page, hide).
        TileEditor.Attach(this);
    }

    bool _disposed;

    /// <summary>The neon tube flickers now and then, like a real one.</summary>
    void StartFlicker()
    {
        var rnd = new Random();
        int step = 0;
        _flicker = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _flicker.Tick += (_, _) =>
        {
            if (step > 0)
            {
                _glow!.Opacity = step % 2 == 0 ? 0.35 : 1;
                step--;
                return;
            }
            _glow!.Opacity = 1;
            if (rnd.NextDouble() < 0.004)
                step = 4 + rnd.Next(4);
        };
        _flicker.Start();
    }

    public void StopAnimations()
    {
        _disposed = true;
        _flicker?.Stop();
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
        var tb = W.Text(text, UI.Theme.Title, UI.Theme.Accent, FontWeight.SemiBold);
        tb.FontFamily = UI.Theme.TitleFont;
        tb.TextTrimming = TextTrimming.CharacterEllipsis; // a long title in a narrow tile ends in "…"
        return tb;
    }
}

/// <summary>Draws a tile's background and frame in the theme's shape.</summary>
public sealed class TileChrome : CachedDrawnControl
{
    readonly bool _outlineOnly;

    public TileChrome(bool outlineOnly)
    {
        _outlineOnly = outlineOnly;
        IsHitTestVisible = false;
    }

    /// <summary>Soft shadow or neon glow around the tile (approximated by its rounded rectangle).</summary>
    public BoxShadows Shadows { get; set; }

    protected override double Bleed => Shadows.Count > 0 ? 24 : 0;

    // Cached as a bitmap: the frame only changes with the theme or size, but is repainted whenever something moves
    // over or under it.
    protected override bool Paint(DrawingContext dc, double w, double h)
    {
        if (w < 4 || h < 4)
            return false;
        var t = UI.Theme.Current;
        IBrush? fill = _outlineOnly ? null : UI.Theme.Tile;
        var borderBrush = _outlineOnly ? UI.Theme.Accent : UI.Theme.TileBorder;
        IPen? border = borderBrush == null ? null : new Pen(borderBrush, _outlineOnly ? 1.5 : t.BorderThickness);
        var r = new Rect(0.5, 0.5, w - 1, h - 1);
        if (Shadows.Count > 0)
        {
            double radius = t.Shape is TileShape.Hud or TileShape.Chamfer or TileShape.Drafting ? 0 : Math.Min(t.Radius, Math.Min(w, h) / 3);
            dc.DrawRectangle(null, null, new RoundedRect(r, radius), Shadows);
        }

        switch (t.Shape)
        {
            case TileShape.Hud:
                dc.DrawRectangle(fill, border, r);
                if (!_outlineOnly)
                    DrawBrackets(dc, r);
                break;
            case TileShape.Chamfer:
                double c = Math.Min(16, Math.Min(w, h) / 5);
                dc.DrawGeometry(fill, border, Polygon(new Point(r.Left + c, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom - c),
                    new Point(r.Right - c, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + c)));
                if (!_outlineOnly)
                {
                    var accent = new Pen(UI.Theme.Accent, 2);
                    dc.DrawLine(accent, new Point(r.Left, r.Top + c), new Point(r.Left + c, r.Top));
                    dc.DrawLine(accent, new Point(r.Right, r.Bottom - c), new Point(r.Right - c, r.Bottom));
                    dc.DrawRectangle(UI.Theme.Accent, null, new Rect(r.Left + c + 4, r.Top, Math.Min(64, w / 4), 3));
                    for (int i = 0; i < 3; i++)
                        dc.DrawRectangle(UI.Theme.Accent, null, new Rect(r.Right - 10 - i * 7, r.Top + 5, 4, 4));
                }
                break;
            case TileShape.Ink:
                dc.DrawRectangle(fill, border, r, t.Radius, t.Radius);
                if (!_outlineOnly)
                {
                    // A brush stroke along the top that fades out, like ink running dry.
                    var stroke = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(UI.Theme.AccentColor, 0), new GradientStop(UI.Theme.WithAlpha(UI.Theme.AccentColor, 0), 1) },
                    };
                    dc.DrawRectangle(stroke, null, new Rect(r.Left + t.Radius, r.Top, w * 0.55, 2.5), 1.2, 1.2);
                }
                break;
            case TileShape.Blade:
                double big = Math.Min(t.Radius, Math.Min(w, h) / 3), small = Math.Min(4, big);
                dc.DrawGeometry(fill, border, Corners(r, big, small, big, small));
                if (!_outlineOnly)
                    for (int i = 0; i < 2; i++)
                    {
                        double x = r.Right - 26 - i * 9;
                        dc.DrawGeometry(UI.Theme.Accent, null, Polygon(new Point(x, r.Top + 6), new Point(x + 5, r.Top + 6), new Point(x + 1, r.Top + 14), new Point(x - 4, r.Top + 14)));
                    }
                break;
            case TileShape.Drafting:
                dc.DrawRectangle(fill, border, r);
                if (!_outlineOnly)
                {
                    var pen = new Pen(UI.Theme.Accent2, 1);
                    const double m = 6;
                    foreach (var (x, y, dx, dy) in new[] { (r.Left, r.Top, -1, -1), (r.Right, r.Top, 1, -1), (r.Left, r.Bottom, -1, 1), (r.Right, r.Bottom, 1, 1) })
                    {
                        dc.DrawLine(pen, new Point(x + dx * 2, y), new Point(x + dx * (2 + m), y));
                        dc.DrawLine(pen, new Point(x, y + dy * 2), new Point(x, y + dy * (2 + m)));
                    }
                    if (w > 120)
                    {
                        double y = r.Bottom - 6, x1 = r.Left + 14, x2 = r.Right - 14;
                        var thin = new Pen(UI.Theme.Solid(UI.Theme.Accent2Color, 0x70), 0.8);
                        dc.DrawLine(thin, new Point(x1, y), new Point(x2, y));
                        dc.DrawLine(thin, new Point(x1, y - 3), new Point(x1, y + 3));
                        dc.DrawLine(thin, new Point(x2, y - 3), new Point(x2, y + 3));
                    }
                }
                break;
            default:
                dc.DrawRectangle(fill, border, r, t.Radius, t.Radius);
                break;
        }
        return true;
    }

    /// <summary>Rectangle with its own radius per corner (top-left, top-right, bottom-right, bottom-left).</summary>
    public static Geometry Corners(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true);
            ctx.LineTo(new Point(r.Right - tr, r.Top));
            ctx.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Right, r.Bottom - br));
            ctx.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom));
            ctx.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Left, r.Top + tl));
            ctx.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return g;
    }

    static void DrawBrackets(DrawingContext dc, Rect r)
    {
        var pen = new Pen(UI.Theme.Accent, 2, lineCap: PenLineCap.Square);
        double len = Math.Min(14, Math.Min(r.Width, r.Height) / 4);
        foreach (var (x, y, dx, dy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
        {
            dc.DrawLine(pen, new Point(x, y), new Point(x + dx * len, y));
            dc.DrawLine(pen, new Point(x, y), new Point(x, y + dy * len));
        }
        dc.DrawLine(new Pen(UI.Theme.Accent, 1), new Point(r.Left + r.Width / 2 - 10, r.Top + 0.5), new Point(r.Left + r.Width / 2 + 10, r.Top + 0.5));
    }

    public static Geometry Polygon(params Point[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], true);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i]);
            ctx.EndFigure(true);
        }
        return g;
    }
}

/// <summary>Thin horizontal progress bar in the theme's meter style.</summary>
public sealed class Meter : DrawnControl
{
    double _value;
    IBrush _fill = UI.Theme.Accent;

    public Meter(double height = 6)
    {
        Height = height;
        Margin = new Thickness(0, 3);
    }

    public void Set(double? percent, IBrush? fill = null)
    {
        _value = Math.Clamp(percent ?? 0, 0, 100);
        _fill = fill ?? UI.Theme.Accent;
        InvalidateVisual();
    }

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        switch (UI.Theme.Current.Meter)
        {
            case MeterStyle.Segmented:
            {
                double cell = Math.Max(4, h * 0.9), gap = 2;
                int n = Math.Max(1, (int)((w + gap) / (cell + gap)));
                double cw = (w - gap * (n - 1)) / n;
                int lit = (int)Math.Round(n * _value / 100);
                for (int i = 0; i < n; i++)
                    dc.DrawRectangle(i < lit ? _fill : UI.Theme.Track, null, new Rect(i * (cw + gap), 0, cw, h));
                break;
            }
            case MeterStyle.Line:
            {
                double track = Math.Max(1, h / 4), fh = Math.Max(2, h * 0.6);
                dc.DrawRectangle(UI.Theme.Track, null, new Rect(0, (h - track) / 2, w, track));
                dc.DrawRectangle(_fill, null, new Rect(0, (h - fh) / 2, w * _value / 100, fh));
                break;
            }
            default:
            {
                double r = h / 2, fw = w * _value / 100;
                dc.DrawRectangle(UI.Theme.Track, null, new Rect(0, 0, w, h), r, r);
                if (fw > 0)
                    dc.DrawRectangle(_fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
                break;
            }
        }
    }
}

/// <summary>Scrolling graph of one or two series from the shared history (e.g. download / upload).</summary>
public sealed class Sparkline : DrawnControl
{
    readonly string _seriesA;
    readonly string? _seriesB;
    readonly int _capacity;
    readonly IBrush _strokeA, _strokeB;
    readonly IBrush _fillA;
    readonly double? _fixedMax;

    public Sparkline(string seriesA, string? seriesB = null, double? fixedMax = 100, IBrush? colorA = null, IBrush? colorB = null, int capacity = 60)
    {
        _seriesA = seriesA;
        _seriesB = seriesB;
        _capacity = capacity;
        _fixedMax = fixedMax;
        var a = (colorA as ISolidColorBrush)?.Color ?? UI.Theme.AccentColor;
        _strokeA = UI.Theme.Solid(a);
        _strokeB = colorB ?? UI.Theme.Solid(UI.Theme.Accent2Color);
        _fillA = Graph.Fade(a, UI.Theme.Current.GraphFill);
        MinHeight = 24;
        Margin = new Thickness(0, 6, 0, 0);
    }

    public void Refresh() => InvalidateVisual();

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawLine(new Pen(UI.Theme.Track, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        var a = Sensors.HistoryStore.Shared.Recent(_seriesA, _capacity);
        var b = _seriesB == null ? Array.Empty<double>() : Sensors.HistoryStore.Shared.Recent(_seriesB, _capacity);
        double max = _fixedMax ?? Math.Max(1, Math.Max(a.DefaultIfEmpty(0).Max(), b.DefaultIfEmpty(0).Max()) * 1.15);
        Graph.Draw(dc, a, _capacity, _strokeA, _fillA, max, w, h);
        Graph.Draw(dc, b, _capacity, _strokeB, null, max, w, h);
    }
}

/// <summary>Line + area drawing shared by the small graphs and the History tile.</summary>
public static class Graph
{
    /// <summary>Vertical fade from the colour (at <paramref name="alpha"/>) to transparent.</summary>
    public static IBrush Fade(Color c, byte alpha) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(UI.Theme.WithAlpha(c, alpha), 0), new GradientStop(UI.Theme.WithAlpha(c, 0), 1) },
    }.ToImmutable();

    public static void Draw(DrawingContext dc, IReadOnlyList<double> data, int capacity, IBrush stroke, IBrush? fill, double max, double w, double h, double min = 0)
    {
        if (data.Count < 2)
            return;
        double step = w / (Math.Max(capacity, data.Count) - 1);
        double Y(double v) => h - Math.Clamp((v - min) / Math.Max(max - min, 1e-9), 0, 1) * (h - 2) - 1;
        double x0 = w - (data.Count - 1) * step;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x0, Y(data[0])), false);
            for (int i = 1; i < data.Count; i++)
                ctx.LineTo(new Point(x0 + i * step, Y(data[i])));
            ctx.EndFigure(false);
        }
        if (fill != null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true);
                for (int i = 0; i < data.Count; i++)
                    ctx.LineTo(new Point(x0 + i * step, Y(data[i])));
                ctx.LineTo(new Point(w, h));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(fill, null, area);
        }
        if (UI.Theme.Current.GraphGlow && stroke is ISolidColorBrush sc)
            dc.DrawGeometry(null, new Pen(UI.Theme.Solid(sc.Color, 60), 6, lineJoin: PenLineJoin.Round), line);
        dc.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round), line);
    }
}

/// <summary>Finger-friendly horizontal slider (0–1).</summary>
public sealed class TouchSlider : DrawnControl
{
    double _value;
    bool _dragging;

    public event Action<double>? Changed;

    /// <summary>Height of the drawn slider; the touch area around it is taller (40 px, a comfortable finger target).</summary>
    const double VisualHeight = 28;

    public TouchSlider()
    {
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public bool IsDragging => _dragging;
    public IBrush? Fill { get; set; }

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _dragging = true;
        e.Pointer.Capture(this);
        SetFrom(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging)
            SetFrom(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => _dragging = false;

    void SetFrom(double x)
    {
        double knob = Math.Min(Bounds.Height, VisualHeight) / 2;
        _value = Math.Clamp((x - knob) / Math.Max(1, Bounds.Width - knob * 2), 0, 1);
        InvalidateVisual();
        Changed?.Invoke(_value);
    }

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, full = Bounds.Height, h = Math.Min(full, VisualHeight);
        if (w < h || h <= 0)
            return; // squeezed narrower than the knob (very narrow column): nothing sensible to draw
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, full)); // whole area is touchable
        dc.PushTransformW(Matrix.CreateTranslation(0, (full - h) / 2)); // the slider itself is drawn centred, at its usual size
        double r = h / 2, track = Math.Max(4, h / 5), x = r + (w - 2 * r) * _value;
        bool square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        double cr = square ? 0 : track / 2;
        var fill = Fill ?? UI.Theme.Accent;
        dc.DrawRectangle(UI.Theme.Track, null, new Rect(r, (h - track) / 2, w - 2 * r, track), cr, cr);
        dc.DrawRectangle(fill, null, new Rect(r, (h - track) / 2, Math.Max(0, x - r), track), cr, cr);
        if (square)
            dc.DrawRectangle(fill, null, new Rect(x - r * 0.4, h * 0.15, r * 0.8, h * 0.7));
        else
            dc.DrawEllipse(UI.Theme.Text, new Pen(fill, 2), new Point(x, h / 2), r * 0.62, r * 0.62);
        dc.PopW();
    }
}

/// <summary>Tap handling with press feedback; acts only when the press started on the element (not on swipes ending there).</summary>
public static class Tap
{
    public static T On<T>(T element, Action action) where T : Control
    {
        bool pressed = false;
        element.Cursor = new Cursor(StandardCursorType.Hand);
        element.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
                return;
            pressed = true;
            element.Opacity = 0.6;
        };
        element.PointerExited += (_, _) =>
        {
            pressed = false;
            element.Opacity = 1;
        };
        element.PointerReleased += (_, e) =>
        {
            element.Opacity = 1;
            // Only a release over the element counts (the pressed element keeps the pointer captured, so it hears
            // releases elsewhere too): a press that slides off a button and lets go does nothing.
            if (!pressed || Dashboard.SwipeInProgress || !new Rect(element.Bounds.Size).Contains(e.GetPosition(element)))
            {
                pressed = false;
                return;
            }
            pressed = false;
            e.Handled = true;
            action();
        };
        return element;
    }
}

/// <summary>Icons: a name (mic, play, game…) becomes a Material Icons glyph; anything else is shown as text / emoji.</summary>
public static class Icon
{
    /// <summary>Shoji's icon names → Material Icons ligature names.</summary>
    public static IReadOnlyDictionary<string, string> Names => Core.DeckIcons.Material;

    public static TextBlock Make(string? icon, double size, IBrush? color = null)
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
        if (Names.TryGetValue(icon, out var ligature))
        {
            t.Text = ligature;
            t.FontFamily = UI.Theme.Icons;
        }
        else
        {
            t.Text = icon;
            t.FontFamily = UI.Theme.Font;
        }
    }

    /// <summary>A Material Icons glyph by its own name ("thermostat", "memory"…).</summary>
    public static TextBlock Glyph(string ligature, double size, IBrush? color = null)
    {
        var t = W.Text(ligature, size, color);
        t.FontFamily = UI.Theme.Icons;
        t.TextTrimming = TextTrimming.None;
        return t;
    }
}

/// <summary>Tiny vertical bars, one per CPU thread.</summary>
public sealed class CoreBars : DrawnControl
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

    protected override void Draw(DrawingContext dc)
    {
        int n = _loads.Count;
        if (n == 0)
            return;
        bool segmented = UI.Theme.Current.Meter == MeterStyle.Segmented;
        double gap = n > 24 ? 1 : 2, h = Bounds.Height;
        double bw = Math.Max((Bounds.Width - gap * (n - 1)) / n, 1);
        for (int i = 0; i < n; i++)
        {
            double x = i * (bw + gap), load = Math.Clamp(_loads[i], 0, 100);
            var fill = UI.Theme.ForPercent(load);
            if (segmented)
            {
                const double cell = 3, cgap = 1;
                int cells = (int)(h / (cell + cgap)), lit = (int)Math.Round(cells * load / 100);
                for (int c = 0; c < cells; c++)
                    dc.DrawRectangle(c < lit ? fill : UI.Theme.Track, null, new Rect(x, h - (c + 1) * (cell + cgap) + cgap, bw, cell));
            }
            else
            {
                dc.DrawRectangle(UI.Theme.Track, null, new Rect(x, 0, bw, h));
                double bh = h * load / 100;
                dc.DrawRectangle(fill, null, new Rect(x, h - bh, bw, bh));
            }
        }
    }
}

/// <summary>Rows of "label …… value" with optional bars, reused between updates.</summary>
public sealed class RowList : StackPanel
{
    readonly bool _withBar;
    readonly List<(TextBlock Label, TextBlock Value, Meter? Bar)> _rows = new();

    public RowList(bool withBar = false) => _withBar = withBar;

    public void Set(int index, string label, string value, double? percent = null, IBrush? valueColor = null, IBrush? barColor = null)
    {
        while (_rows.Count <= index)
        {
            var row = W.Row("", out var v, out var l);
            l.Foreground = UI.Theme.Text;
            Meter? bar = _withBar ? new Meter(5) { Margin = new Thickness(0, 1, 0, 6) } : null;
            Children.Add(row);
            if (bar != null)
                Children.Add(bar);
            _rows.Add((l, v, bar));
        }
        var (lb, vb, mb) = _rows[index];
        lb.Text = label;
        vb.Text = value;
        vb.Foreground = valueColor ?? UI.Theme.Text;
        mb?.Set(percent, barColor ?? UI.Theme.ForPercent(percent));
    }

    public void Trim(int count)
    {
        int perRow = _withBar ? 2 : 1;
        while (_rows.Count > count)
        {
            _rows.RemoveAt(_rows.Count - 1);
            Children.RemoveRange(Children.Count - perRow, perRow);
        }
    }
}

/// <summary>Horizontal bar split into coloured parts (power breakdown).</summary>
public sealed class StackBar : DrawnControl
{
    IReadOnlyList<(double Value, IBrush Brush)> _parts = Array.Empty<(double, IBrush)>();

    public StackBar() => Height = 10;

    public void Set(IReadOnlyList<(double Value, IBrush Brush)> parts)
    {
        _parts = parts;
        InvalidateVisual();
    }

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height, total = _parts.Sum(p => Math.Max(0, p.Value));
        if (w <= 0 || total <= 0)
            return;
        bool square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        using (dc.PushClip(new RoundedRect(new Rect(0, 0, w, h), square ? 0 : h / 2)))
        {
            double x = 0;
            foreach (var (value, brush) in _parts)
            {
                double pw = w * Math.Max(0, value) / total;
                dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(0, pw - 1.5), h));
                x += pw;
            }
        }
    }
}
