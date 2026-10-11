using System;
using System.Collections.Generic;
#if SHOJI_LINUX
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BrushT = Avalonia.Media.IBrush;
using Ctx = Shoji.UI.FigureWriter;
#else
using System.Windows;
using System.Windows.Media;
using BrushT = System.Windows.Media.Brush;
using Ctx = System.Windows.Media.StreamGeometryContext;
#endif

namespace Shoji.UI;

/// <summary>
/// Backdrop art for the newer themes, written once for both dashboards (WPF on Windows, Avalonia on Linux):
/// only drawing calls the two share are used, plus the small helpers at the bottom.
/// Every scene has a still version (<see cref="Draw"/>, behind the tiles, redrawn only on resize or theme change)
/// and, where it moves, a frame for the animation layer (<see cref="Frame"/>, ~20 fps while animations are on).
/// Everything is faint on purpose: it shows in the gaps between tiles and through translucent tiles.
/// </summary>
public static class BackdropArt
{
    /// <summary>Styles drawn here (the older ones live in Backdrop).</summary>
    public static bool Handles(BackdropStyle s) => s is BackdropStyle.Bamboo or BackdropStyle.Blobs or BackdropStyle.Ocean
        or BackdropStyle.Sunset or BackdropStyle.Paper or BackdropStyle.Dots or BackdropStyle.Wave;

    /// <summary>Styles with something that moves (drawn by the animation layer while animations are on).</summary>
    public static bool HasMotion(BackdropStyle s) => s is BackdropStyle.Bamboo or BackdropStyle.Blobs or BackdropStyle.Ocean
        or BackdropStyle.Sunset or BackdropStyle.Wave;

    /// <summary>The still layer. With animations on, the moving parts are left to <see cref="Frame"/>.</summary>
    public static void Draw(DrawingContext dc, BackdropStyle style, double w, double h)
    {
        bool still = !Theme.Animate;
        switch (style)
        {
            case BackdropStyle.Bamboo: Bamboo(dc, w, h, 0, still); break;
            case BackdropStyle.Blobs: if (still) Blobs(dc, w, h, 0); break;
            case BackdropStyle.Ocean: Ocean(dc, w, h, 0, still); break;
            case BackdropStyle.Sunset: Sunset(dc, w, h, 0, sceneOnly: false, still); break;
            case BackdropStyle.Paper: Paper(dc, w, h); break;
            case BackdropStyle.Dots: Dots(dc, w, h); break;
            case BackdropStyle.Wave: Wave(dc, w, h, 0, still); break;
        }
    }

    /// <summary>One animation frame at time <paramref name="t"/> (seconds).</summary>
    public static void Frame(DrawingContext dc, BackdropStyle style, double w, double h, double t)
    {
        switch (style)
        {
            case BackdropStyle.Bamboo: FallingLeaves(dc, w, h, t); break;
            case BackdropStyle.Blobs: Blobs(dc, w, h, t); break;
            case BackdropStyle.Ocean: OceanMotion(dc, w, h, t); break;
            case BackdropStyle.Sunset: Sunset(dc, w, h, t, sceneOnly: true, still: false); break;
            case BackdropStyle.Wave: WaveMotion(dc, w, h, t); break;
        }
    }

    // ================================================================ bamboo (Matcha)

    static void Bamboo(DrawingContext dc, double w, double h, double t, bool still)
    {
        var rnd = new Random(7);
        // Washi paper: short faint fibres.
        var fibre = Line(Solid(Theme.TextColor, 0x0A), 0.8);
        for (int i = 0; i < 160; i++)
        {
            double x = rnd.NextDouble() * w, y = rnd.NextDouble() * h, len = 6 + rnd.NextDouble() * 18, a = rnd.NextDouble() * Math.PI;
            dc.DrawGeometry(null, fibre, Shape(c =>
            {
                c.BeginFigure(new Point(x, y), false, false);
                c.QuadraticBezierTo(new Point(x + Math.Cos(a) * len / 2 + 3, y + Math.Sin(a) * len / 2 - 3), new Point(x + Math.Cos(a) * len, y + Math.Sin(a) * len), true, false);
            }));
        }
        // A few stalks at each edge, nodes every so often, leaves near the top.
        double unit = Math.Max(10, Math.Min(w, h) * 0.028);
        foreach (var (fx, scale, lean) in new[] { (0.025, 1.0, -0.02), (0.075, 0.8, 0.015), (0.93, 0.9, 0.02), (0.975, 1.1, -0.015) })
        {
            double x0 = w * fx, sw = unit * scale;
            var stalk = Solid(Theme.AccentColor, 0x26);
            var node = Solid(Theme.AccentColor, 0x40);
            double y = h + 10;
            int seg = 0;
            while (y > -40)
            {
                double len = h * (0.18 + rnd.NextDouble() * 0.08);
                double x = x0 + (h - y) * lean;
                RoundRect(dc, stalk, null, new Rect(x - sw / 2, y - len, sw, len - 3), sw * 0.4);
                RoundRect(dc, node, null, new Rect(x - sw * 0.62, y - len - 2, sw * 1.24, 3.5), 1.5);
                if (seg >= 1 && rnd.NextDouble() < 0.6)
                    for (int k = 0; k < 3; k++)
                        Leaf(dc, new Point(x, y - len), unit * (3.2 + rnd.NextDouble() * 1.8), (fx < 0.5 ? -20 : 200) + rnd.Next(-35, 35) + k * (fx < 0.5 ? 18 : -18), Solid(Theme.AccentColor, 0x30));
                y -= len;
                seg++;
            }
        }
        if (still)
            FallingLeaves(dc, w, h, 0);
    }

    static void FallingLeaves(DrawingContext dc, double w, double h, double t)
    {
        for (int i = 0; i < LeafCount; i++)
        {
            var (at, angle) = LeafAt(i, w, h, t);
            Leaf(dc, at, Leaves[i].Length, angle, Solid(Theme.AccentColor, Leaves[i].Alpha));
        }
    }

    /// <summary>The falling leaves: each one's fall period, phase, sideways drift, start (fraction of the width), size and opacity.</summary>
    static readonly (double Period, double Phase, double Drift, double X0, double Length, byte Alpha)[] Leaves = MakeLeaves();

    public const int LeafCount = 9;

    static (double, double, double, double, double, byte)[] MakeLeaves()
    {
        var rnd = new Random(11);
        var leaves = new (double, double, double, double, double, byte)[LeafCount];
        for (int i = 0; i < LeafCount; i++)
            leaves[i] = (16 + rnd.NextDouble() * 12, rnd.NextDouble(), 60 + rnd.NextDouble() * 160, rnd.NextDouble(),
                16 + rnd.NextDouble() * 10, (byte)(0x38 + rnd.Next(0x30)));
        return leaves;
    }

    /// <summary>Where falling leaf <paramref name="i"/> is at time <paramref name="t"/>, and which way it points (degrees).</summary>
    public static (Point At, double Angle) LeafAt(int i, double w, double h, double t)
    {
        var l = Leaves[i];
        double f = Frac(t / l.Period + l.Phase);
        double x = l.X0 * w + l.Drift * f + 18 * Math.Sin(t * 0.9 + i), y = -30 + (h + 60) * f;
        return (new Point(x, y), 30 * Math.Sin(t * 0.7 + i * 1.3) + i * 40);
    }

    /// <summary>Falling leaf <paramref name="i"/> at the origin pointing right, and its brush (for drawing it as a moving sprite).</summary>
    public static (Geometry Shape, BrushT Brush) LeafSprite(int i) =>
        (LeafShape(new Point(0, 0), Leaves[i].Length, 0), Solid(Theme.AccentColor, Leaves[i].Alpha));

    /// <summary>A narrow bamboo leaf from <paramref name="at"/>, pointing along <paramref name="angle"/> (degrees).</summary>
    static void Leaf(DrawingContext dc, Point at, double length, double angle, BrushT brush) =>
        dc.DrawGeometry(brush, null, LeafShape(at, length, angle));

    static Geometry LeafShape(Point at, double length, double angle)
    {
        double a = angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        Point P(double u, double v) => new(at.X + (u * cos - v * sin) * length, at.Y + (u * sin + v * cos) * length);
        return Shape(c =>
        {
            c.BeginFigure(P(0, 0), true, true);
            c.QuadraticBezierTo(P(0.45, -0.16), P(1, 0), true, false);
            c.QuadraticBezierTo(P(0.45, 0.12), P(0, 0), true, false);
        });
    }

    // ================================================================ blobs (Glass)

    /// <summary>Big soft colour fields that drift slowly behind frosted tiles.</summary>
    static void Blobs(DrawingContext dc, double w, double h, double t)
    {
        double r = Math.Max(w, h);
        var spots = new[]
        {
            (Theme.AccentColor, 0.18, 0.25, 0.55, 23.0, 0.0),
            (Theme.Accent2Color, 0.82, 0.7, 0.5, 29.0, 1.7),
            (Theme.Parse(Theme.Current.Good), 0.55, 0.05, 0.38, 35.0, 3.1),
            (Theme.Parse(Theme.Current.Warn), 0.35, 0.95, 0.34, 41.0, 4.4),
        };
        foreach (var (color, fx, fy, fr, period, phase) in spots)
        {
            double x = w * fx + w * 0.12 * Math.Sin(2 * Math.PI * t / period + phase);
            double y = h * fy + h * 0.15 * Math.Cos(2 * Math.PI * t / (period * 1.3) + phase);
            double rad = r * fr * (1 + 0.06 * Math.Sin(t / 5 + phase));
            dc.DrawEllipse(Radial((Theme.WithAlpha(color, 0x8C), 0), (Theme.WithAlpha(color, 0x30), 0.55), (Theme.WithAlpha(color, 0), 1)), null, new Point(x, y), rad, rad);
        }
    }

    // ================================================================ ocean (Deep Sea)

    static void Ocean(DrawingContext dc, double w, double h, double t, bool still)
    {
        // Sea floor: two dark ridges and a few kelp strands.
        var floor = Shape(c =>
        {
            c.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i <= 40; i++)
            {
                double x = w * i / 40.0;
                c.LineTo(new Point(x, h * 0.9 - Math.Sin(i * 0.45) * h * 0.035 - Math.Sin(i * 0.17 + 1) * h * 0.05), true, false);
            }
            c.LineTo(new Point(w, h), true, false);
        });
        dc.DrawGeometry(Solid(Color.FromRgb(0, 0, 0), 0x55), null, floor);
        var rnd = new Random(5);
        for (int k = 0; k < 7; k++)
        {
            double x = w * (0.05 + rnd.NextDouble() * 0.9), top = h * (0.62 + rnd.NextDouble() * 0.15);
            var kelp = Line(Solid(Theme.Accent2Color, 0x1C), 3 + rnd.NextDouble() * 3);
            dc.DrawGeometry(null, kelp, Shape(c =>
            {
                c.BeginFigure(new Point(x, h), false, false);
                for (int s = 1; s <= 8; s++)
                    c.LineTo(new Point(x + Math.Sin(s * 0.9 + k) * 10, h - (h - top) * s / 8), true, true);
            }));
        }
        if (still)
            OceanMotion(dc, w, h, 0);
    }

    static void OceanMotion(DrawingContext dc, double w, double h, double t)
    {
        // Light rays from the surface, slowly breathing.
        var rnd = new Random(3);
        for (int i = 0; i < 6; i++)
        {
            double x = w * (0.1 + i * 0.16 + rnd.NextDouble() * 0.05), spread = w * (0.05 + rnd.NextDouble() * 0.06);
            double glow = 0.55 + 0.45 * Math.Sin(t * 0.35 + i * 1.7);
            var ray = Linear(new Point(0, 0), new Point(0, 1), (Theme.WithAlpha(Theme.TextColor, (byte)(0x1A * glow)), 0), (Theme.WithAlpha(Theme.TextColor, 0), 1));
            dc.DrawGeometry(ray, null, Shape(c =>
            {
                c.BeginFigure(new Point(x - 12, 0), true, true);
                c.LineTo(new Point(x + 12, 0), true, false);
                c.LineTo(new Point(x + spread + 30 * Math.Sin(t * 0.1 + i), h * 0.85), true, false);
                c.LineTo(new Point(x - spread + 30 * Math.Sin(t * 0.1 + i), h * 0.85), true, false);
            }));
        }
        // Bioluminescent motes rising and swaying.
        var mote = new Random(17);
        for (int i = 0; i < 46; i++)
        {
            double period = 18 + mote.NextDouble() * 22, phase = mote.NextDouble(), x0 = mote.NextDouble() * w, size = 1.2 + mote.NextDouble() * 2.2;
            var color = i % 3 == 0 ? Theme.Accent2Color : Theme.AccentColor;
            double f = Frac(t / period + phase);
            double x = x0 + 14 * Math.Sin(t * 0.6 + i), y = h + 10 - (h + 20) * f;
            double twinkle = 0.5 + 0.5 * Math.Sin(t * 2 + i * 2.3);
            dc.DrawEllipse(Solid(color, (byte)(0x22 * twinkle)), null, new Point(x, y), size * 3.5, size * 3.5);
            dc.DrawEllipse(Solid(color, (byte)(0x60 + 0x60 * twinkle)), null, new Point(x, y), size, size);
        }
        // One jellyfish drifting up and down on the right.
        double jx = w * 0.84 + 20 * Math.Sin(t * 0.07), jy = h * 0.42 + h * 0.12 * Math.Sin(t * 0.11), pulse = 1 + 0.08 * Math.Sin(t * 1.6);
        var bell = Solid(Theme.Accent2Color, 0x2E);
        double bw = Math.Min(w, h) * 0.07 * pulse, bh = bw * 0.75;
        dc.DrawGeometry(bell, Line(Solid(Theme.Accent2Color, 0x55), 1.2), Shape(c =>
        {
            c.BeginFigure(new Point(jx - bw, jy), true, true);
            c.BezierTo(new Point(jx - bw, jy - bh * 1.4), new Point(jx + bw, jy - bh * 1.4), new Point(jx + bw, jy), true, false);
            c.QuadraticBezierTo(new Point(jx, jy + bh * 0.25), new Point(jx - bw, jy), true, false);
        }));
        var tentacle = Line(Solid(Theme.Accent2Color, 0x30), 1.2);
        for (int k = 0; k < 5; k++)
        {
            double sx = jx - bw * 0.7 + bw * 1.4 * k / 4;
            dc.DrawGeometry(null, tentacle, Shape(c =>
            {
                c.BeginFigure(new Point(sx, jy + 2), false, false);
                for (int s = 1; s <= 6; s++)
                    c.LineTo(new Point(sx + 5 * Math.Sin(t * 1.4 + s * 0.8 + k), jy + 2 + s * bh * 0.55), true, true);
            }));
        }
    }

    // ================================================================ sunset (Lo-fi Sunset)

    /// <summary>A striped retro sun sinking into layered hills; the stripes slowly scroll.</summary>
    static void Sunset(DrawingContext dc, double w, double h, double t, bool sceneOnly, bool still)
    {
        if (!sceneOnly && !still)
        {
            Hills(dc, w, h); // the sun is drawn by the animation layer, the hills on top of it there too
            return;
        }
        double r = Math.Min(h * 0.3, w * 0.2), cx = w * 0.7, cy = h * 0.7;
        var sun = Linear(new Point(0, 0), new Point(0, 1), (Theme.Parse(Theme.Current.Warn), 0), (Theme.Accent2Color, 1));
        dc.DrawEllipse(Radial((Theme.WithAlpha(Theme.Parse(Theme.Current.Warn), 0x40), 0), (Theme.WithAlpha(Theme.Accent2Color, 0), 1)), null, new Point(cx, cy), r * 1.9, r * 1.9);
        // Horizontal gaps across the lower half that grow toward the bottom and drift down.
        var stripes = new GeometryGroup();
        stripes.Children.Add(new RectangleGeometry(new Rect(cx - r, cy - r, r * 2, r * 1.05)));
        double shift = Frac(t / 6) * r * 0.16;
        for (int i = 0; i < 8; i++)
        {
            double y = cy + r * 0.05 + i * r * 0.16 + shift, gap = 2 + i * 1.6;
            stripes.Children.Add(new RectangleGeometry(new Rect(cx - r, y + gap, r * 2, Math.Max(0, r * 0.16 - gap))));
        }
        WithClip(dc, stripes, () => dc.DrawEllipse(sun, null, new Point(cx, cy), r, r));
        Hills(dc, w, h);
        // Specks of dust catching the light.
        var rnd = new Random(9);
        for (int i = 0; i < 24; i++)
        {
            double period = 20 + rnd.NextDouble() * 20, phase = rnd.NextDouble(), x0 = rnd.NextDouble() * w, y0 = rnd.NextDouble() * h * 0.7;
            double f = Frac(t / period + phase);
            double alpha = Math.Sin(f * Math.PI);
            dc.DrawEllipse(Solid(Theme.Parse(Theme.Current.Warn), (byte)(0x70 * alpha)), null, new Point(x0 + 40 * f, y0 - 30 * f), 1.4, 1.4);
        }
    }

    static void Hills(DrawingContext dc, double w, double h)
    {
        var layers = new[] { (0.74, 0.06, 0.8, (byte)0x70), (0.82, 0.05, 1.7, (byte)0xA0), (0.9, 0.04, 2.9, (byte)0xD0) };
        foreach (var (baseY, amp, phase, alpha) in layers)
        {
            var col = Mix(Theme.Accent2Color, Theme.BackgroundColor, 0.75);
            dc.DrawGeometry(Solid(col, alpha), null, Shape(c =>
            {
                c.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i <= 48; i++)
                {
                    double x = w * i / 48.0;
                    c.LineTo(new Point(x, h * baseY - Math.Sin(i * 0.22 + phase) * h * amp - Math.Sin(i * 0.53 + phase * 2) * h * amp * 0.4), true, false);
                }
                c.LineTo(new Point(w, h), true, false);
            }));
        }
    }

    // ================================================================ paper (Notebook)

    /// <summary>Squared notebook paper: fine grid, a red margin, punched holes and a faint coffee ring.</summary>
    static void Paper(DrawingContext dc, double w, double h)
    {
        const double step = 24;
        var rule = Line(Solid(Color.FromRgb(0x7F, 0xA8, 0xD8), 0x38), 0.8, round: false);
        for (double x = step; x < w; x += step)
            dc.DrawLine(rule, new Point(x, 0), new Point(x, h));
        for (double y = step; y < h; y += step)
            dc.DrawLine(rule, new Point(0, y), new Point(w, y));
        double margin = Math.Min(80, w * 0.06);
        dc.DrawLine(Line(Solid(Theme.Accent2Color, 0x70), 1.4, round: false), new Point(margin, 0), new Point(margin, h));
        var hole = Solid(Mix(Theme.BackgroundColor, Color.FromRgb(0, 0, 0), 0.12), 0xFF);
        foreach (var f in new[] { 0.18, 0.5, 0.82 })
            dc.DrawEllipse(hole, Line(Solid(Theme.TextColor, 0x18), 1), new Point(margin / 2.6, h * f), 7, 7);
        // Coffee ring, half there.
        var coffee = Color.FromRgb(0x8B, 0x5A, 0x2B);
        double cr = Math.Min(w, h) * 0.12, ccx = w * 0.86, ccy = h * 0.22;
        dc.DrawEllipse(null, Line(Solid(coffee, 0x16), 6), new Point(ccx, ccy), cr, cr);
        dc.DrawGeometry(null, Line(Solid(coffee, 0x22), 2.5), Shape(c =>
        {
            c.BeginFigure(new Point(ccx + cr, ccy), false, false);
            c.ArcTo(new Point(ccx - cr * 0.6, ccy + cr * 0.8), new Size(cr, cr), 0, false, SweepDirection.Clockwise, true, false);
        }));
    }

    // ================================================================ dots (editor palettes)

    /// <summary>A quiet dot grid, like an editor's canvas, with two corners lit in the theme colours.</summary>
    static void Dots(DrawingContext dc, double w, double h)
    {
        dc.DrawEllipse(Radial((Theme.WithAlpha(Theme.AccentColor, 0x22), 0), (Theme.WithAlpha(Theme.AccentColor, 0), 1)), null, new Point(0, 0), w * 0.55, h * 0.9);
        dc.DrawEllipse(Radial((Theme.WithAlpha(Theme.Accent2Color, 0x1C), 0), (Theme.WithAlpha(Theme.Accent2Color, 0), 1)), null, new Point(w, h), w * 0.55, h * 0.9);
        var dot = Solid(Theme.TextColor, 0x16);
        const double step = 22;
        for (double y = step / 2; y < h; y += step)
            for (double x = step / 2; x < w; x += step)
                dc.DrawEllipse(dot, null, new Point(x, y), 1.1, 1.1);
    }

    // ================================================================ wave (Kanagawa)

    /// <summary>Seigaiha wave scales along the bottom, a great wave rising on the left, a pale moon.</summary>
    static void Wave(DrawingContext dc, double w, double h, double t, bool still)
    {
        double mr = Math.Min(w, h) * 0.13;
        var moon = new Point(w * 0.86, h * 0.2);
        dc.DrawEllipse(Radial((Theme.WithAlpha(Theme.TextColor, 0x16), 0), (Theme.WithAlpha(Theme.TextColor, 0), 1)), null, moon, mr * 2.2, mr * 2.2);
        dc.DrawEllipse(Solid(Theme.TextColor, 0x16), Line(Solid(Theme.TextColor, 0x22), 1.2), moon, mr, mr);
        Seigaiha(dc, w, h);
        if (still)
            WaveMotion(dc, w, h, 0);
    }

    static void Seigaiha(DrawingContext dc, double w, double h)
    {
        double r = Math.Max(18, Math.Min(w, h) * 0.05);
        var fill = Solid(Theme.BackgroundColor, 0xFF);
        var ring = Line(Solid(Theme.Accent2Color, 0x30), 1.1);
        double top = h * 0.62;
        int row = 0;
        for (double y = top; y < h + r; y += r * 0.5, row++)
        {
            double offset = row % 2 == 0 ? 0 : r;
            for (double x = -r + offset; x < w + r; x += r * 2)
            {
                // Fade the pattern in from its top edge.
                double fade = Math.Clamp((y - top) / (r * 2.5), 0.25, 1);
                WithOpacity(dc, fade, () =>
                {
                    for (int k = 0; k < 4; k++)
                    {
                        double rr = r * (1 - k * 0.24);
                        // The outer scale is filled (covering the row behind it); only the arcs are outlined, not the flat bottom.
                        if (k == 0)
                            dc.DrawGeometry(fill, null, Shape(c =>
                            {
                                c.BeginFigure(new Point(x - rr, y), true, true);
                                c.ArcTo(new Point(x + rr, y), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
                            }));
                        dc.DrawGeometry(null, ring, Shape(c =>
                        {
                            c.BeginFigure(new Point(x - rr, y), false, false);
                            c.ArcTo(new Point(x + rr, y), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
                        }));
                    }
                });
            }
        }
    }

    /// <summary>The great wave's crest: a curling body with foam claws that bob and glint.</summary>
    static void WaveMotion(DrawingContext dc, double w, double h, double t)
    {
        double s = Math.Min(w, h), bob = 4 * Math.Sin(t * 0.6), x0 = -s * 0.05, y0 = h * 0.98 + bob;
        Point P(double u, double v) => new(x0 + u * s, y0 - v * s);
        var body = Shape(c =>
        {
            c.BeginFigure(P(0, 0), true, true);
            c.BezierTo(P(0.05, 0.35), P(0.25, 0.62), P(0.48, 0.6), true, false);
            c.BezierTo(P(0.62, 0.59), P(0.68, 0.48), P(0.6, 0.42), true, false);
            c.BezierTo(P(0.54, 0.38), P(0.47, 0.45), P(0.5, 0.5), true, false);
            c.BezierTo(P(0.38, 0.5), P(0.3, 0.3), P(0.42, 0.1), true, false);
            c.BezierTo(P(0.5, 0.02), P(0.65, 0), P(0.75, 0), true, false);
        });
        dc.DrawGeometry(Solid(Theme.Accent2Color, 0x1E), Line(Solid(Theme.Accent2Color, 0x40), 1.4), body);
        // Inner swirl lines.
        var swirl = Line(Solid(Theme.Accent2Color, 0x2C), 1);
        for (int k = 1; k <= 3; k++)
        {
            double d = k * 0.06;
            dc.DrawGeometry(null, swirl, Shape(c =>
            {
                c.BeginFigure(P(0.08 + d, 0.02), false, false);
                c.BezierTo(P(0.12 + d, 0.3 - d), P(0.28 + d * 0.5, 0.5 - d), P(0.46, 0.55 - d * 0.8), true, false);
            }));
        }
        // Foam claws along the crest.
        for (int i = 0; i < 9; i++)
        {
            // Along the crest (it peaks around u = 0.48), each claw a little bigger toward the curl.
            double u = 0.22 + i * 0.045, v = 0.56 - Math.Pow((u - 0.48) / 0.3, 2) * 0.12;
            double glint = 0.6 + 0.4 * Math.Sin(t * 1.8 + i * 1.1);
            dc.DrawEllipse(Solid(Theme.TextColor, (byte)(0x48 * glint)), null, P(u, v), s * (0.008 + i * 0.0012), s * (0.008 + i * 0.0012));
        }
    }

    // ================================================================ helpers (the WPF / Avalonia differences)

    static double Frac(double x) => x - Math.Floor(x);

    public static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    static BrushT Solid(Color c, byte alpha)
    {
#if SHOJI_LINUX
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
#else
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
#endif
    }

    static Pen Line(BrushT brush, double thickness, bool round = true)
    {
#if SHOJI_LINUX
        return new Pen(brush, thickness, lineCap: round ? PenLineCap.Round : PenLineCap.Flat, lineJoin: PenLineJoin.Round);
#else
        var p = new Pen(brush, thickness) { StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat, EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
#endif
    }

    /// <summary>A gradient between two relative points (0–1 across the shape's bounds).</summary>
    static BrushT Linear(Point from, Point to, params (Color Color, double Offset)[] stops)
    {
#if SHOJI_LINUX
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(from, RelativeUnit.Relative), EndPoint = new RelativePoint(to, RelativeUnit.Relative) };
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(c, o));
        return b;
#else
        var b = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(c, o));
        b.Freeze();
        return b;
#endif
    }

    static BrushT Radial(params (Color Color, double Offset)[] stops)
    {
        var b = new RadialGradientBrush();
        foreach (var (c, o) in stops)
            b.GradientStops.Add(new GradientStop(c, o));
#if !SHOJI_LINUX
        b.Freeze();
#endif
        return b;
    }

    static Geometry Shape(Action<Ctx> draw)
    {
        var g = new StreamGeometry();
#if SHOJI_LINUX
        using (var c = g.OpenW())
            draw(c);
#else
        using (var c = g.Open())
            draw(c);
        g.Freeze();
#endif
        return g;
    }

    static void RoundRect(DrawingContext dc, BrushT? brush, Pen? pen, Rect r, double radius)
    {
#if SHOJI_LINUX
        dc.DrawRectangle(brush, pen, r, radius, radius);
#else
        dc.DrawRoundedRectangle(brush, pen, r, radius, radius);
#endif
    }

    static void WithOpacity(DrawingContext dc, double opacity, Action draw)
    {
#if SHOJI_LINUX
        using (dc.PushOpacity(opacity))
            draw();
#else
        dc.PushOpacity(opacity);
        draw();
        dc.Pop();
#endif
    }

    static void WithClip(DrawingContext dc, Geometry clip, Action draw)
    {
#if SHOJI_LINUX
        using (dc.PushGeometryClip(clip))
            draw();
#else
        dc.PushClip(clip);
        draw();
        dc.Pop();
#endif
    }
}
