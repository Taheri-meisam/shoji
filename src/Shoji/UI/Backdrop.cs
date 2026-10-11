using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Shoji.UI;

/// <summary>
/// Decorative full-screen layer behind the tiles (grid, scanlines, synthwave floor, ink circle, petals…).
/// It's static, so it is only redrawn when the window size or theme changes. Everything is drawn faintly:
/// it shows in the gaps between tiles and through translucent tiles.
/// </summary>
public sealed class Backdrop : FrameworkElement
{
    public Backdrop() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        switch (Theme.Current.Backdrop)
        {
            case BackdropStyle.Glow: Glow(dc, w, h); break;
            case BackdropStyle.Grid: Grid(dc, w, h); break;
            case BackdropStyle.Scanlines: Scanlines(dc, w, h); break;
            case BackdropStyle.Synthwave: Synthwave(dc, w, h); break;
            case BackdropStyle.Ink: Ink(dc, w, h); break;
            case BackdropStyle.Sakura: Sakura(dc, w, h); break;
            case BackdropStyle.Carbon: Carbon(dc, w, h); break;
            case BackdropStyle.Space: Space(dc, w, h); break;
            case BackdropStyle.Aurora: Aurora(dc, w, h); break;
            case BackdropStyle.Blueprint: Blueprint(dc, w, h); break;
            case BackdropStyle.Kintsugi: Kintsugi(dc, w, h); break;
            case BackdropStyle.Fjord: Fjord(dc, w, h); break;
            default:
                if (BackdropArt.Handles(Theme.Current.Backdrop))
                    BackdropArt.Draw(dc, Theme.Current.Backdrop, w, h);
                break;
        }
    }

    // ---------------------------------------------------------------- aurora

    /// <summary>Soft curtains of light: wide wavy bands, bright at the bottom edge and fading upward.</summary>
    public static Geometry AuroraBand(double w, double h, double baseY, double amp, double phase, double height)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            const int steps = 48;
            Point Edge(int i) => new(w * i / steps, baseY + Math.Sin(i / (double)steps * Math.PI * 2.2 + phase) * amp + Math.Sin(i * 0.37 + phase * 2) * amp * 0.3);
            ctx.BeginFigure(new Point(0, baseY - height), true, true);
            for (int i = 0; i <= steps; i++)
                ctx.LineTo(Edge(i) with { Y = Edge(i).Y - height }, false, false);
            for (int i = steps; i >= 0; i--)
                ctx.LineTo(Edge(i), false, false);
        }
        g.Freeze();
        return g;
    }

    public static Brush AuroraFill(Color c, byte alpha)
    {
        var b = new LinearGradientBrush(Theme.WithAlpha(c, 0), Theme.WithAlpha(c, alpha), 90);
        b.Freeze();
        return b;
    }

    static void Aurora(DrawingContext dc, double w, double h)
    {
        var rnd = new Random(5);
        for (int i = 0; i < 120; i++)
        {
            double z = rnd.NextDouble();
            dc.DrawEllipse(B(Colors.White, (byte)(0x30 + z * 0x70)), null, new Point(rnd.NextDouble() * w, rnd.NextDouble() * h * 0.7), 0.4 + z * 0.8, 0.4 + z * 0.8);
        }
        dc.DrawGeometry(AuroraFill(Theme.AccentColor, 0x30), null, AuroraBand(w, h, h * 0.55, h * 0.06, 0.4, h * 0.35));
        dc.DrawGeometry(AuroraFill(Theme.Accent2Color, 0x24), null, AuroraBand(w, h, h * 0.42, h * 0.05, 2.1, h * 0.28));
        // Dark horizon and a faint reflection.
        var ground = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xC0, 2, 6, 12), 90);
        ground.Freeze();
        dc.DrawRectangle(ground, null, new Rect(0, h * 0.78, w, h * 0.22));
    }

    // ---------------------------------------------------------------- blueprint

    static void Blueprint(DrawingContext dc, double w, double h)
    {
        if (w < 40 || h < 40)
            return; // sheet border and title block need some room
        var minor = P(Colors.White, 0x0E, 1);
        var major = P(Colors.White, 0x22, 1);
        for (double x = 0.5, i = 0; x < w; x += 10, i++)
            dc.DrawLine(i % 5 == 0 ? major : minor, new Point(x, 0), new Point(x, h));
        for (double y = 0.5, i = 0; y < h; y += 10, i++)
            dc.DrawLine(i % 5 == 0 ? major : minor, new Point(0, y), new Point(w, y));

        // Construction circles and centre lines, like a part drawing in the background.
        var line = P(Colors.White, 0x2A, 1);
        var dashed = new Pen(B(Colors.White, 0x30), 1) { DashStyle = new DashStyle(new double[] { 8, 3, 2, 3 }, 0) };
        dashed.Freeze();
        var c = new Point(w * 0.72, h * 0.4);
        double r = Math.Min(w, h) * 0.22;
        dc.DrawEllipse(null, line, c, r, r);
        dc.DrawEllipse(null, line, c, r * 0.55, r * 0.55);
        dc.DrawLine(dashed, new Point(c.X - r * 1.3, c.Y), new Point(c.X + r * 1.3, c.Y));
        dc.DrawLine(dashed, new Point(c.X, c.Y - r * 1.3), new Point(c.X, c.Y + r * 1.3));

        // Sheet border and title block in the bottom-right corner.
        var border = P(Colors.White, 0x60, 1.5);
        dc.DrawRectangle(null, border, new Rect(4, 4, w - 8, h - 8));
        double bw = Math.Min(260, w * 0.4), bh = 54, bx = w - 4 - bw, by = h - 4 - bh;
        dc.DrawRectangle(B(Theme.BackgroundColor, 0xE0), border, new Rect(bx, by, bw, bh));
        dc.DrawLine(border, new Point(bx, by + 22), new Point(bx + bw, by + 22));
        dc.DrawLine(border, new Point(bx + bw * 0.55, by + 22), new Point(bx + bw * 0.55, by + bh));
        var face = new Typeface(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        void Label(string text, double x, double y, double size, byte alpha) =>
            dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, B(Colors.White, alpha), 1.25), new Point(x, y));
        Label("SHOJI · SYSTEM MONITOR", bx + 8, by + 4, 12, 0xC0);
        Label("SHEET 1 OF 1", bx + 8, by + 28, 10, 0x90);
        Label("SCALE 1:1", bx + 8, by + 40, 10, 0x90);
        Label(DateTime.Today.ToString("yyyy-MM-dd"), bx + bw * 0.55 + 8, by + 28, 10, 0x90);
        Label("REV 1.8", bx + bw * 0.55 + 8, by + 40, 10, 0x90);
    }

    // ---------------------------------------------------------------- kintsugi

    /// <summary>Crack lines mended with gold: a few branching, slightly jagged seams across the lacquer.</summary>
    public static Geometry Cracks(double w, double h, int seed)
    {
        var rnd = new Random(seed);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            void Seam(Point p, double angle, double length, int depth)
            {
                ctx.BeginFigure(p, false, false);
                double travelled = 0;
                while (travelled < length)
                {
                    double step = 18 + rnd.NextDouble() * 30;
                    angle += (rnd.NextDouble() - 0.5) * 0.7;
                    p = new Point(p.X + Math.Cos(angle) * step, p.Y + Math.Sin(angle) * step);
                    ctx.LineTo(p, true, true);
                    travelled += step;
                    if (depth < 2 && rnd.NextDouble() < 0.18)
                    {
                        var branchFrom = p;
                        Seam(branchFrom, angle + (rnd.NextDouble() < 0.5 ? -1 : 1) * (0.5 + rnd.NextDouble() * 0.6), length * 0.35, depth + 1);
                        ctx.BeginFigure(p, false, false);
                    }
                }
            }
            Seam(new Point(-10, h * 0.25), 0.25, w * 1.2, 0);
            Seam(new Point(w * 0.65, -10), 1.9, h * 1.2, 0);
            Seam(new Point(w + 10, h * 0.8), Math.PI + 0.15, w * 0.6, 0);
        }
        g.Freeze();
        return g;
    }

    static void Kintsugi(DrawingContext dc, double w, double h)
    {
        // Lacquer sheen.
        dc.DrawEllipse(Radial(Theme.Accent2Color, 0x10), null, new Point(w * 0.3, h * 0.2), w * 0.6, h * 0.5);
        var seams = Cracks(w, h, 23);
        var gold = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x9C, 0x74, 0x2A), 0), new(Color.FromRgb(0xF2, 0xD3, 0x7A), 0.5), new(Color.FromRgb(0xB8, 0x8A, 0x35), 1),
        }, 35);
        gold.Freeze();
        var halo = new Pen(B(Theme.AccentColor, 0x24), 7) { LineJoin = PenLineJoin.Round };
        halo.Freeze();
        var seam = new Pen(gold, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        seam.Freeze();
        dc.DrawGeometry(null, halo, seams);
        dc.DrawGeometry(null, seam, seams);
    }

    // ---------------------------------------------------------------- fjord

    static void Fjord(DrawingContext dc, double w, double h)
    {
        dc.DrawEllipse(B(Color.FromRgb(0xE5, 0xE9, 0xF0), 0x30), null, new Point(w * 0.78, h * 0.22), Math.Min(w, h) * 0.06, Math.Min(w, h) * 0.06);
        dc.DrawEllipse(Radial(Color.FromRgb(0xE5, 0xE9, 0xF0), 0x18), null, new Point(w * 0.78, h * 0.22), Math.Min(w, h) * 0.2, Math.Min(w, h) * 0.2);
        // Three ridgelines, lighter in the distance.
        var colors = new[] { (Color.FromRgb(0x4C, 0x56, 0x6A), (byte)0x70), (Color.FromRgb(0x43, 0x4C, 0x5E), (byte)0xA0), (Color.FromRgb(0x2E, 0x34, 0x40), (byte)0xE0) };
        var rnd = new Random(8);
        for (int layer = 0; layer < 3; layer++)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double baseY = h * (0.62 + layer * 0.1);
                ctx.BeginFigure(new Point(0, h), true, true);
                ctx.LineTo(new Point(0, baseY), false, false);
                double x = 0;
                while (x < w)
                {
                    double peakW = w * (0.08 + rnd.NextDouble() * 0.12);
                    double peakH = h * (0.06 + rnd.NextDouble() * 0.14) / (layer + 1) * 1.6;
                    ctx.LineTo(new Point(x + peakW / 2, baseY - peakH), false, false);
                    x += peakW;
                    ctx.LineTo(new Point(x, baseY - peakH * rnd.NextDouble() * 0.3), false, false);
                }
                ctx.LineTo(new Point(w, h), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(B(colors[layer].Item1, colors[layer].Item2), null, g);
        }
    }

    // ---------------------------------------------------------------- hypercar

    /// <summary>Twill carbon-fibre weave with a soft sheen across it.</summary>
    static void Carbon(DrawingContext dc, double w, double h)
    {
        var cell = new DrawingGroup();
        using (var c = cell.Open())
        {
            var dark = B(Colors.Black, 0x00);
            c.DrawRectangle(dark, null, new Rect(0, 0, 8, 8));
            var light = B(Colors.White, 0x09);
            var lighter = B(Colors.White, 0x05);
            c.DrawRectangle(light, null, new Rect(0, 0, 4, 2));
            c.DrawRectangle(lighter, null, new Rect(0, 2, 4, 2));
            c.DrawRectangle(light, null, new Rect(4, 4, 4, 2));
            c.DrawRectangle(lighter, null, new Rect(4, 6, 4, 2));
            c.DrawRectangle(B(Colors.White, 0x04), null, new Rect(4, 0, 2, 4));
            c.DrawRectangle(B(Colors.White, 0x04), null, new Rect(0, 4, 2, 4));
        }
        cell.Freeze();
        var weave = new DrawingBrush(cell)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(45),
        };
        weave.Freeze();
        dc.DrawRectangle(weave, null, new Rect(0, 0, w, h));

        var sheen = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 255, 255, 255), 0.25),
            new(Color.FromArgb(0x14, 255, 255, 255), 0.5),
            new(Color.FromArgb(0, 255, 255, 255), 0.75),
        }, 30);
        sheen.Freeze();
        dc.DrawRectangle(sheen, null, new Rect(0, 0, w, h));
        dc.DrawEllipse(Radial(Theme.AccentColor, 0x18), null, new Point(w * 0.5, h * 1.05), w * 0.6, h * 0.35);
    }

    // ---------------------------------------------------------------- deep space

    static void Space(DrawingContext dc, double w, double h)
    {
        dc.DrawEllipse(Radial(Theme.Accent2Color, 0x2C), null, new Point(w * 0.78, h * 0.18), w * 0.55, h * 0.5);
        dc.DrawEllipse(Radial(Theme.AccentColor, 0x26), null, new Point(w * 0.12, h * 0.85), w * 0.6, h * 0.55);
        dc.DrawEllipse(Radial(Color.FromRgb(0x6B, 0x3F, 0xD6), 0x20), null, new Point(w * 0.45, h * 0.5), w * 0.5, h * 0.4);

        var rnd = new Random(11);
        for (int i = 0; i < (int)(w * h / 5000); i++)
        {
            double z = rnd.NextDouble();
            var col = z > 0.92 ? Color.FromRgb(0xFF, 0xD9, 0xF0) : Color.FromRgb(0xDF, 0xE6, 0xFF);
            dc.DrawEllipse(B(col, (byte)(0x40 + z * 0xA0)), null, new Point(rnd.NextDouble() * w, rnd.NextDouble() * h), 0.4 + z, 0.4 + z);
        }
        // A few bright stars with a cross glint.
        for (int i = 0; i < 7; i++)
        {
            var p = new Point(rnd.NextDouble() * w, rnd.NextDouble() * h);
            var glint = P(Colors.White, 0x60, 1);
            dc.DrawLine(glint, new Point(p.X - 7, p.Y), new Point(p.X + 7, p.Y));
            dc.DrawLine(glint, new Point(p.X, p.Y - 7), new Point(p.X, p.Y + 7));
            dc.DrawEllipse(B(Colors.White, 0xE0), null, p, 1.4, 1.4);
        }
    }

    static Brush B(Color c, byte alpha) => Theme.Freeze(new SolidColorBrush(Theme.WithAlpha(c, alpha)));

    static Pen P(Color c, byte alpha, double thickness)
    {
        var p = new Pen(B(c, alpha), thickness);
        p.Freeze();
        return p;
    }

    static Brush Radial(Color c, byte alpha)
    {
        var b = new RadialGradientBrush(Theme.WithAlpha(c, alpha), Theme.WithAlpha(c, 0));
        b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- modern

    static void Glow(DrawingContext dc, double w, double h)
    {
        double r = Math.Max(w, h) * 0.55;
        dc.DrawEllipse(Radial(Theme.AccentColor, 0x30), null, new Point(w * 0.15, h * 0.12), r, r);
        dc.DrawEllipse(Radial(Theme.Accent2Color, 0x24), null, new Point(w * 0.9, h * 0.88), r, r);
    }

    // ---------------------------------------------------------------- futuristic

    static void Grid(DrawingContext dc, double w, double h)
    {
        const double step = 28;
        var minor = P(Theme.AccentColor, 0x0E, 1);
        var major = P(Theme.AccentColor, 0x1C, 1);
        int i = 0;
        for (double x = 0.5; x < w; x += step, i++)
            dc.DrawLine(i % 4 == 0 ? major : minor, new Point(x, 0), new Point(x, h));
        i = 0;
        for (double y = 0.5; y < h; y += step, i++)
            dc.DrawLine(i % 4 == 0 ? major : minor, new Point(0, y), new Point(w, y));

        // Crosshairs on major intersections.
        var cross = P(Theme.AccentColor, 0x40, 1);
        for (double x = 0.5; x < w; x += step * 4)
            for (double y = 0.5; y < h; y += step * 4)
            {
                dc.DrawLine(cross, new Point(x - 4, y), new Point(x + 4, y));
                dc.DrawLine(cross, new Point(x, y - 4), new Point(x, y + 4));
            }

        var vignette = new RadialGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xA0, 0, 0, 0))
        {
            RadiusX = 0.75, RadiusY = 0.75,
        };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, new Rect(0, 0, w, h));
    }

    // ---------------------------------------------------------------- cyberpunk

    static void Scanlines(DrawingContext dc, double w, double h)
    {
        var line = B(Colors.White, 0x06);
        for (double y = 0; y < h; y += 3)
            dc.DrawRectangle(line, null, new Rect(0, y, w, 1));

        // A few seeded "glitch" slivers so it looks the same every time.
        var rnd = new Random(2077);
        for (int i = 0; i < 14; i++)
        {
            var c = i % 3 == 0 ? Theme.Accent2Color : Theme.AccentColor;
            double gw = 20 + rnd.NextDouble() * 220, gh = 1 + rnd.Next(5);
            dc.DrawRectangle(B(c, 0x1C), null, new Rect(rnd.NextDouble() * (w - gw), rnd.NextDouble() * h, gw, gh));
        }

        // Hazard stripes along the bottom-left edge.
        double sw = Math.Min(220, w / 3), sh = 6;
        dc.PushClip(new RectangleGeometry(new Rect(6, h - sh - 2, sw, sh)));
        var stripe = B(Theme.AccentColor, 0x55);
        for (double x = -sh; x < sw + 12; x += 12)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(6 + x, h - 2), true, true);
                ctx.LineTo(new Point(6 + x + 6, h - 2), false, false);
                ctx.LineTo(new Point(6 + x + 6 + sh, h - sh - 2), false, false);
                ctx.LineTo(new Point(6 + x + sh, h - sh - 2), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(stripe, null, g);
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- tokyo neon

    void Synthwave(DrawingContext dc, double w, double h)
    {
        double horizon = h * 0.6;

        // Sun with the classic horizontal cuts.
        double sr = Math.Min(w, h) * 0.24;
        var sunCenter = new Point(w / 2, horizon - sr * 0.3);
        var sun = new LinearGradientBrush(Color.FromArgb(0x60, 0xFF, 0xD3, 0x19), Theme.WithAlpha(Theme.AccentColor, 0x60), 90);
        sun.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, horizon)));
        dc.DrawEllipse(sun, null, sunCenter, sr, sr);
        var cut = Theme.Freeze(new SolidColorBrush(Theme.Background2Color));
        for (int i = 0; i < 6; i++)
        {
            double y = sunCenter.Y + sr * (0.05 + i * 0.16);
            dc.DrawRectangle(cut, null, new Rect(sunCenter.X - sr, y, sr * 2, 2 + i * 1.6));
        }
        dc.Pop();

        // Perspective floor.
        dc.DrawLine(P(Theme.Accent2Color, 0x90, 1.5), new Point(0, horizon), new Point(w, horizon));
        const int rows = 12;
        for (int k = 1; k <= rows; k++)
        {
            double t = (double)k / rows;
            double y = horizon + (h - horizon) * t * t;
            dc.DrawLine(P(Theme.AccentColor, (byte)(0x18 + 0x50 * t), 1), new Point(0, y), new Point(w, y));
        }
        const int cols = 14;
        for (int i = -cols; i <= cols; i++)
        {
            double top = w / 2 + i * (w / cols) * 0.08;
            double bottom = w / 2 + i * (w / cols) * 1.3;
            dc.DrawLine(P(Theme.AccentColor, 0x40, 1), new Point(top, horizon), new Point(bottom, h));
        }

        // Vertical neon signs on the edges.
        double size = Math.Clamp(h * 0.05, 22, 64);
        VerticalSign(dc, "ネオン", new Point(size * 0.4, h * 0.08), size, Theme.AccentColor);
        VerticalSign(dc, "東京", new Point(w - size * 1.4, h * 0.12), size, Theme.Accent2Color);
    }

    void VerticalSign(DrawingContext dc, string text, Point origin, double size, Color color)
    {
        var face = new Typeface(Theme.TitleFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double y = origin.Y;
        foreach (char ch in text)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, B(color, 0x38), dpi);
            dc.DrawText(ft, new Point(origin.X, y));
            y += size * 1.1;
        }
    }

    // ---------------------------------------------------------------- sumi-e

    static void Ink(DrawingContext dc, double w, double h)
    {
        // Ensō: one brush circle, thick in the middle of the stroke and tapering at both ends, left open.
        double r = Math.Min(w, h) * 0.3;
        var center = new Point(w * 0.76, h * 0.2);
        const int n = 160;
        double start = 110 * Math.PI / 180, sweep = 320 * Math.PI / 180;
        for (int i = 0; i < n; i++)
        {
            double t0 = (double)i / n, t1 = (double)(i + 1) / n;
            double a0 = start + sweep * t0, a1 = start + sweep * t1;
            double thick = r * 0.1 * (0.25 + 0.75 * Math.Pow(Math.Sin(Math.PI * Math.Min(1, t0 * 1.15)), 0.5));
            var pen = new Pen(B(Theme.TextColor, 0x10), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, new Point(center.X + r * Math.Cos(a0), center.Y + r * Math.Sin(a0)),
                             new Point(center.X + r * Math.Cos(a1), center.Y + r * Math.Sin(a1)));
        }

        Seigaiha(dc, w, h, Theme.Accent2Color, 0x30);
    }

    /// <summary>Seigaiha (blue ocean waves): overlapping fans of concentric arcs, as a band along the bottom.</summary>
    static void Seigaiha(DrawingContext dc, double w, double h, Color color, byte alpha)
    {
        const double r = 28;
        var pen = P(color, alpha, 1);
        var fill = Theme.Freeze(new SolidColorBrush(Theme.Background2Color));
        int rows = 5;
        for (int row = 0; row < rows; row++)
        {
            double cy = h - (rows - 1 - row) * r * 0.5;
            double offset = row % 2 == 0 ? 0 : r;
            for (double cx = -r + offset; cx < w + r; cx += r * 2)
            {
                for (int ring = 4; ring >= 1; ring--)
                {
                    double rr = r * ring / 4;
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(cx - rr, cy), true, true);
                        ctx.ArcTo(new Point(cx + rr, cy), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(ring == 4 ? fill : null, pen, g);
                }
            }
        }
    }

    // ---------------------------------------------------------------- sakura

    static void Sakura(DrawingContext dc, double w, double h)
    {
        var rnd = new Random(3);
        for (int i = 0; i < 46; i++)
        {
            double s = 6 + rnd.NextDouble() * 10;
            var p = new Point(rnd.NextDouble() * w, rnd.NextDouble() * h);
            Petal(dc, p, s, rnd.NextDouble() * 360, B(Theme.AccentColor, (byte)(0x22 + rnd.Next(0x30))));
        }
        for (int i = 0; i < 7; i++)
        {
            var p = new Point(rnd.NextDouble() * w, rnd.NextDouble() * h);
            double s = 10 + rnd.NextDouble() * 12;
            var brush = B(Theme.AccentColor, 0x30);
            double rot = rnd.NextDouble() * 72;
            for (int k = 0; k < 5; k++)
            {
                double a = (rot + k * 72) * Math.PI / 180;
                Petal(dc, new Point(p.X + Math.Cos(a) * s * 0.55, p.Y + Math.Sin(a) * s * 0.55), s, rot + k * 72, brush);
            }
            dc.DrawEllipse(B(Theme.Accent2Color, 0x40), null, p, s * 0.12, s * 0.12);
        }
        Seigaiha(dc, w, h, Theme.Accent2Color, 0x24);
    }

    /// <summary>A cherry petal: an oval with a small notch at the tip, rotated.</summary>
    static void Petal(DrawingContext dc, Point at, double size, double angle, Brush brush)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double l = size, wd = size * 0.55;
            ctx.BeginFigure(new Point(-l / 2, 0), true, true);
            ctx.BezierTo(new Point(-l / 4, -wd), new Point(l / 2, -wd * 0.8), new Point(l / 2, -wd * 0.15), true, false);
            ctx.LineTo(new Point(l / 2 - l * 0.12, 0), true, false);
            ctx.LineTo(new Point(l / 2, wd * 0.15), true, false);
            ctx.BezierTo(new Point(l / 2, wd * 0.8), new Point(-l / 4, wd), new Point(-l / 2, 0), true, false);
        }
        g.Transform = new TransformGroup { Children = { new RotateTransform(angle), new TranslateTransform(at.X, at.Y) } };
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }
}

/// <summary>Cassette theme: CRT scanlines and a curved-glass vignette over the whole screen (drawn once, never hit-tested).</summary>
public sealed class CrtOverlay : FrameworkElement
{
    public CrtOverlay() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (!Theme.Current.Crt || w <= 0 || h <= 0)
            return;
        var line = new DrawingGroup();
        using (var c = line.Open())
        {
            c.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 4, 3));
            c.DrawRectangle(Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0))), null, new Rect(0, 0, 4, 1));
        }
        line.Freeze();
        var scan = new DrawingBrush(line) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 3), ViewportUnits = BrushMappingMode.Absolute };
        scan.Freeze();
        dc.DrawRectangle(scan, null, new Rect(0, 0, w, h));

        var vignette = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 0, 0, 0), 0.6),
            new(Color.FromArgb(0xA0, 0, 0, 0), 1),
        }) { RadiusX = 0.62, RadiusY = 0.62 };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, new Rect(0, 0, w, h));
    }
}
