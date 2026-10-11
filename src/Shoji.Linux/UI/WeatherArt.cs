using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Shoji.UI;

/// <summary>
/// A drawn, gently animated weather icon: the sun's rays turn, clouds drift, rain and snow fall, lightning flashes,
/// fog slides, stars twinkle. Colours follow the theme. With animations off (Settings → Appearance) it's a still picture.
/// All icons share one 20 fps timer that runs only while one of them is on screen.
/// </summary>
public sealed class WeatherArt : DrawnControl
{
    enum Sky { Sun, Moon, PartSun, PartMoon, Cloud, Overcast, Fog, Drizzle, Rain, HeavyRain, Sleet, Snow, Showers, SnowShowers, Thunder, Unknown }

    static readonly HashSet<WeatherArt> Live = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    // Shapes in the 1×1 drawing box, built once. They're made at 100× size and scaled down, because WPF
    // flattens curves to a fixed tolerance (0.25 units) that would make tiny shapes polygonal.
    static readonly Geometry CloudShape = MakeCloud();
    static readonly Geometry Crescent = Unit(new CombinedGeometry(GeometryCombineMode.Exclude,
        DrawCompat.Ellipse(new Point(0, 0), 26, 26), DrawCompat.Ellipse(new Point(13, -8), 22, 22)));
    static readonly Geometry StarShape = MakeStar();
    static readonly Geometry Bolt = MakeBolt();

    // Brushes and pens follow the theme; rebuilt only when its colours change (not every frame).
    static (Color Text, Color Back, Color Accent)? _paletteFor;
    static IBrush _front = Brushes.White, _back = Brushes.Gray, _flake = Brushes.White, _sun = Brushes.Gold, _moon = Brushes.Beige, _bolt = Brushes.Gold;
    static Pen _drop = new(Brushes.SkyBlue, 0.035), _fog = new(Brushes.White, 0.05);

    Sky _sky = Sky.Unknown;
    readonly double _phase = Random.Shared.NextDouble() * 10; // so icons side by side don't move in step

    public WeatherArt(double size)
    {
        Width = size * 1.15;
        Height = size;
        AttachedToVisualTree += (_, _) => Track(true);
        DetachedFromVisualTree += (_, _) => Track(false);
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Track(VisualRoot != null); };
    }

    /// <summary>Shows the weather for a WMO code (Open-Meteo), as day or night.</summary>
    public void Set(int code, bool day = true)
    {
        var sky = code switch
        {
            0 => day ? Sky.Sun : Sky.Moon,
            1 => day ? Sky.Sun : Sky.Moon,
            2 => day ? Sky.PartSun : Sky.PartMoon,
            3 => Sky.Overcast,
            45 or 48 => Sky.Fog,
            51 or 53 or 55 => Sky.Drizzle,
            56 or 57 or 66 or 67 => Sky.Sleet,
            61 or 63 => Sky.Rain,
            65 => Sky.HeavyRain,
            71 or 73 or 75 or 77 => Sky.Snow,
            80 or 81 or 82 => Sky.Showers,
            85 or 86 => Sky.SnowShowers,
            95 or 96 or 99 => Sky.Thunder,
            _ => Sky.Cloud,
        };
        if (sky == _sky)
            return;
        _sky = sky;
        InvalidateVisual();
        Track(VisualRoot != null);
    }

    void Track(bool on)
    {
        on = on && IsVisible && UI.Theme.Animate && _sky != Sky.Unknown;
        if (on ? !Live.Add(this) : !Live.Remove(this))
            return;
        if (Live.Count > 0)
            FrameClock.Subscribe(Frame);
        else
            FrameClock.Unsubscribe(Frame);
    }

    static void Frame()
    {
        foreach (var art in Live)
            if (art.IsEffectivelyVisible) // not the icons on hidden pages
                art.InvalidateVisual();
    }

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || _sky == Sky.Unknown)
            return;
        // Time in seconds; a fixed, nicely posed moment when animations are off.
        double t = UI.Theme.Animate ? Clock.Elapsed.TotalSeconds + _phase : 0.35;
        Palette();
        double s = Math.Min(w / 1.15, h);
        dc.PushTransformW(DrawCompat.Translate((w - s) / 2, (h - s) / 2));
        dc.PushTransformW(DrawCompat.Scale(s, s));
        // Everything below is drawn in a 1×1 box.
        switch (_sky)
        {
            case Sky.Sun: Sun(dc, 0.5, 0.5, 1, t); break;
            case Sky.Moon: Moon(dc, 0.5, 0.48, 1, t); break;
            case Sky.PartSun:
                Sun(dc, 0.36, 0.36, 0.72, t);
                Cloud(dc, 0.56 + 0.02 * Math.Sin(t * 0.6), 0.62, 0.78, Front);
                break;
            case Sky.PartMoon:
                Moon(dc, 0.36, 0.34, 0.7, t);
                Cloud(dc, 0.56 + 0.02 * Math.Sin(t * 0.6), 0.62, 0.78, Front);
                break;
            case Sky.Cloud:
                Cloud(dc, 0.5 + 0.03 * Math.Sin(t * 0.6), 0.52, 1, Front);
                break;
            case Sky.Overcast:
                Cloud(dc, 0.62 - 0.03 * Math.Sin(t * 0.5), 0.38, 0.8, Back);
                Cloud(dc, 0.44 + 0.03 * Math.Sin(t * 0.6), 0.58, 0.95, Front);
                break;
            case Sky.Fog: Fog(dc, t); break;
            case Sky.Drizzle: Rainy(dc, t, drops: 3, speed: 0.9, length: 0.05); break;
            case Sky.Rain: Rainy(dc, t, drops: 5, speed: 1.3, length: 0.09); break;
            case Sky.HeavyRain: Rainy(dc, t, drops: 8, speed: 1.8, length: 0.12, dark: true); break;
            case Sky.Sleet:
                Rainy(dc, t, drops: 3, speed: 1.2, length: 0.08);
                Flakes(dc, t, 3, 0.5);
                break;
            case Sky.Snow:
                Cloud(dc, 0.5 + 0.02 * Math.Sin(t * 0.5), 0.36, 0.9, Front);
                Flakes(dc, t, 6, 0);
                break;
            case Sky.Showers:
                Sun(dc, 0.32, 0.3, 0.6, t);
                Rainy(dc, t, drops: 4, speed: 1.3, length: 0.09, cloudX: 0.56);
                break;
            case Sky.SnowShowers:
                Sun(dc, 0.32, 0.3, 0.6, t);
                Cloud(dc, 0.56 + 0.02 * Math.Sin(t * 0.5), 0.42, 0.85, Front);
                Flakes(dc, t, 4, 0, cx: 0.56);
                break;
            case Sky.Thunder: Thunder(dc, t); break;
        }
        dc.PopW();
        dc.PopW();
    }

    // ---------------------------------------------------------------- colours (from the theme)

    static IBrush Front => _front;
    static IBrush Back => _back;

    static void Palette()
    {
        var key = (UI.Theme.TextColor, UI.Theme.BackgroundColor, UI.Theme.AccentColor);
        if (key == _paletteFor)
            return;
        _paletteFor = key;
        _front = Solid(UI.Theme.TextColor, 0xF0);
        _back = Solid(Mix(UI.Theme.TextColor, UI.Theme.BackgroundColor, 0.45), 0xE0);
        _flake = Solid(UI.Theme.TextColor, 0xFF);
        _sun = Solid(Mix(Color.FromRgb(0xFF, 0xC8, 0x3D), UI.Theme.AccentColor, 0.1), 0xFF);
        _moon = Solid(Mix(Color.FromRgb(0xF6, 0xEC, 0xC4), UI.Theme.AccentColor, 0.06), 0xFF);
        _bolt = Solid(Color.FromRgb(0xFF, 0xD5, 0x4A), 0xFF);
        _drop = Frozen(new Pen(Solid(Mix(Color.FromRgb(0x6E, 0xC1, 0xFF), UI.Theme.AccentColor, 0.5), 0xFF), 0.035) { LineCap = PenLineCap.Round, });
        _fog = Frozen(new Pen(Solid(UI.Theme.TextColor, 0xC8), 0.05) { LineCap = PenLineCap.Round, });
    }

    static T Frozen<T>(T f) => f; // Avalonia brushes and pens need no freezing

    static IBrush Solid(Color c, byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

    static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    // ---------------------------------------------------------------- pieces

    static void Sun(DrawingContext dc, double cx, double cy, double k, double t)
    {
        // Soft glow that breathes, a disc, and rays turning slowly.
        double pulse = 0.5 + 0.5 * Math.Sin(t * 1.6);
        dc.PushOpacityW((0x22 + 0x1C * pulse) / 255.0);
        dc.DrawEllipse(_sun, null, new Point(cx, cy), 0.3 * k, 0.3 * k);
        dc.PopW();
        var ray = Frozen(new Pen(_sun, 0.045 * k) { LineCap = PenLineCap.Round, });
        double turn = t * 0.35;
        for (int i = 0; i < 8; i++)
        {
            double a = turn + i * Math.PI / 4, r1 = 0.27 * k, r2 = (0.37 + (i % 2 == 0 ? 0.03 : 0)) * k;
            dc.DrawLine(ray, new Point(cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1), new Point(cx + Math.Cos(a) * r2, cy + Math.Sin(a) * r2));
        }
        dc.DrawEllipse(_sun, null, new Point(cx, cy), 0.19 * k, 0.19 * k);
    }

    static void Moon(DrawingContext dc, double cx, double cy, double k, double t)
    {
        // Twinkling stars, then a crescent (a disc with a bite taken out).
        for (int i = 0; i < 3; i++)
        {
            double tw = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * 2.2 + i * 2.1));
            var (sx, sy, sr) = i switch { 0 => (0.82, 0.22, 0.035), 1 => (0.74, 0.62, 0.025), _ => (0.2, 0.2, 0.022) };
            dc.PushOpacityW(tw);
            Place(dc, StarShape, cx + (sx - 0.5) * k, cy + (sy - 0.5) * k, sr * k, _moon);
            dc.PopW();
        }
        Place(dc, Crescent, cx, cy, k, _moon);
    }

    /// <summary>Draws a unit shape (built around 0, 0) at a position and size.</summary>
    static void Place(DrawingContext dc, Geometry shape, double x, double y, double k, IBrush brush)
    {
        dc.PushTransformW(new MatrixTransform(new Matrix(k, 0, 0, k, x, y)));
        dc.DrawGeometry(brush, null, shape);
        dc.PopW();
    }

    /// <summary>Scales a shape built at 100× down to the drawing box, frozen.</summary>
    static Geometry Unit(Geometry g)
    {
        g.Transform = new ScaleTransform(0.01, 0.01);
        return g;
    }

    static Geometry MakeStar()
    {
        // A four-pointed sparkle of radius 1 (built at radius 100).
        var g = new StreamGeometry();
        using (var ctx = g.OpenW())
        {
            ctx.BeginFigure(new Point(0, -100), true, true);
            ctx.QuadraticBezierTo(new Point(0, 0), new Point(100, 0), true, false);
            ctx.QuadraticBezierTo(new Point(0, 0), new Point(0, 100), true, false);
            ctx.QuadraticBezierTo(new Point(0, 0), new Point(-100, 0), true, false);
            ctx.QuadraticBezierTo(new Point(0, 0), new Point(0, -100), true, false);
        }
        return Unit(g);
    }

    static Geometry MakeBolt()
    {
        var g = new StreamGeometry();
        using (var ctx = g.OpenW())
        {
            ctx.BeginFigure(new Point(0.53, 0.5), true, true);
            foreach (var (x, y) in new[] { (0.4, 0.74), (0.5, 0.74), (0.44, 0.94), (0.64, 0.66), (0.53, 0.66), (0.6, 0.5) })
                ctx.LineTo(new Point(x, y), true, false);
        }
        return g;
    }

    static void Cloud(DrawingContext dc, double cx, double cy, double k, IBrush brush)
    {
        dc.PushTransformW(new MatrixTransform(new Matrix(k, 0, 0, k, cx, cy)));
        dc.DrawGeometry(brush, null, CloudShape);
        dc.PopW();
    }

    static Geometry MakeCloud()
    {
        // Three bumps on a rounded base, centred on (0, 0), merged into one outline so a translucent fill has no seams.
        var g = new GeometryGroup { FillRule = FillRule.NonZero };
        g.Children.Add(DrawCompat.Ellipse(new Point(-19, 4), 14, 14));
        g.Children.Add(DrawCompat.Ellipse(new Point(0, -5), 20, 20));
        g.Children.Add(DrawCompat.Ellipse(new Point(19, 5), 13, 13));
        g.Children.Add(new RectangleGeometry(new Rect(-33, 2, 66, 16), 8, 8));
        // Union them into one outline, so a translucent fill has no seams.
        Geometry merged = g.Children[0];
        for (int i = 1; i < g.Children.Count; i++)
            merged = new CombinedGeometry(GeometryCombineMode.Union, merged, g.Children[i]);
        return Unit(merged);
    }

    /// <summary>A cloud with drops falling from it on a loop, each at its own pace.</summary>
    static void Rainy(DrawingContext dc, double t, int drops, double speed, double length, bool dark = false, double cloudX = 0.5)
    {
        var drop = _drop;
        for (int i = 0; i < drops; i++)
        {
            double lane = cloudX - 0.24 + 0.48 * (i + 0.5) / drops;
            double f = Frac(t * speed + i * 0.37);
            double y = 0.58 + f * 0.36, x = lane - f * 0.05; // slight slant
            using var _ = Fade(dc, f);
            dc.DrawLine(drop, new Point(x, y), new Point(x - length * 0.4, y + length));
        }
        Cloud(dc, cloudX + 0.02 * Math.Sin(t * 0.5), 0.4, 0.92, dark ? Back : Front);
        if (dark)
            Cloud(dc, cloudX + 0.06 + 0.02 * Math.Sin(t * 0.7), 0.46, 0.62, Front);
    }

    static void Flakes(DrawingContext dc, double t, int count, double offset, double cx = 0.5)
    {
        var flake = _flake;
        for (int i = 0; i < count; i++)
        {
            double lane = cx - 0.24 + 0.48 * (i + 0.5 + offset) / (count + offset);
            double f = Frac(t * 0.35 + i * 0.29 + offset);
            double y = 0.56 + f * 0.38, x = lane + 0.025 * Math.Sin(t * 2 + i * 1.7);
            using var _ = Fade(dc, f);
            dc.DrawEllipse(flake, null, new Point(x, y), 0.028, 0.028);
        }
    }

    static void Fog(DrawingContext dc, double t)
    {
        Cloud(dc, 0.5, 0.34, 0.8, Back);
        var pen = _fog;
        for (int i = 0; i < 3; i++)
        {
            double y = 0.56 + i * 0.13, dx = 0.06 * Math.Sin(t * 0.7 + i * 1.9), half = 0.3 - i * 0.04;
            dc.DrawLine(pen, new Point(0.5 - half + dx, y), new Point(0.5 + half + dx, y));
        }
    }

    static void Thunder(DrawingContext dc, double t)
    {
        // A bolt flashes twice every few seconds; the cloud lights up with it.
        double cycle = Frac(t / 3.4) * 3.4;
        bool flash = cycle < 0.12 || cycle is > 0.24 and < 0.36;
        Rainy(dc, t, drops: 4, speed: 1.5, length: 0.09, dark: !flash);
        if (!flash && UI.Theme.Animate)
            return;
        dc.DrawGeometry(_bolt, null, Bolt);
    }

    static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Fades a falling drop or flake in at the top and out at the bottom.</summary>
    static Popper Fade(DrawingContext dc, double f)
    {
        double a = f < 0.15 ? f / 0.15 : f > 0.75 ? (1 - f) / 0.25 : 1;
        dc.PushOpacityW(Math.Clamp(a, 0, 1));
        return new Popper(dc);
    }

    readonly struct Popper : IDisposable
    {
        readonly DrawingContext _dc;
        public Popper(DrawingContext dc) => _dc = dc;
        public void Dispose() => _dc.PopW();
    }
}
