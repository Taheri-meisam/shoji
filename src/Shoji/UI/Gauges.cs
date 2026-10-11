using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shoji.UI;

/// <summary>Colours and fonts a gauge draws with, taken from the active theme when the gauge is created.</summary>
public sealed class GaugePalette
{
    public Brush Fg = Theme.Text, Dim = Theme.Dim, Track = Theme.Track, Accent = Theme.Accent, Accent2 = Theme.Accent2;
    public Brush Warn = Theme.Warn, Crit = Theme.Crit, Good = Theme.Good, Face = Theme.Tile;
    public FontFamily Display = Theme.Display, Mono = Theme.Mono, Font = Theme.Font;
    /// <summary>Hairline arcs (Capsule).</summary>
    public bool Thin = Theme.Current.ThinGauges;
    /// <summary>Slanted numbers (Hypercar).</summary>
    public bool Italic = Theme.Current.ItalicNumbers;
    /// <summary>Faint wide stroke under bright marks (neon / phosphor themes).</summary>
    public bool Glow = Theme.Current.TextGlow || Theme.Current.TileGlow || Theme.Current.GraphGlow;
    /// <summary>Ink colour for the ensō brush.</summary>
    public Brush Ink = Theme.Current.Japanese ? Theme.Text : Theme.Accent;
}

/// <summary>
/// Base for the gauges: values ease toward their targets like a real instrument (needles don't jump),
/// and redraws are driven by one shared ~30 fps timer only while something moves, the gauge is visible, and
/// (for always-moving gauges such as the radar) animations are on. The timer stops when nothing moves.
/// </summary>
public abstract class Gauge : FrameworkElement
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    protected static double Now => Clock.Elapsed.TotalSeconds;

    // Gauges that want frames, and the one timer that drives them (runs only while the set isn't empty).
    static readonly HashSet<Gauge> Live = new();
    static DispatcherTimer? _timer;
    // Frozen pens shared by every gauge: the same few (brush, width) pairs are drawn every frame.
    static readonly Dictionary<(Brush, double, PenLineCap), Pen> Pens = new();

    protected readonly GaugePalette P = new();
    readonly Dictionary<string, double> _shown = new(), _target = new();
    readonly Dictionary<(Color, byte), Brush> _alpha = new();
    double _lastTick;

    /// <summary>True for gauges that animate on their own (radar sweep, orbits, liquid surface).</summary>
    protected virtual bool Continuous => false;

    /// <summary>How quickly shown values follow new readings (higher = snappier).</summary>
    protected virtual double Speed => 6;

    /// <summary>Span of the readings; a value has arrived once it is within 0.1 % of this of its target.</summary>
    protected virtual double Range => 100;

    // While motion is paused (screen off, PC locked, night mode black) the timer stops; on resume every gauge
    // jumps straight to its latest reading instead of sweeping up from where it froze.
    static Gauge() => Motion.Changed += () =>
    {
        if (_timer == null)
            return;
        if (Motion.Paused)
        {
            _timer.Stop();
            return;
        }
        foreach (var g in Live.ToArray())
            g.Snap();
        _timer.Start();
    };

    protected Gauge()
    {
        MinHeight = 40;
        Loaded += (_, _) => { if (WantsFrames) Hook(); };
        Unloaded += (_, _) => Unhook();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && WantsFrames) Hook();
            InvalidateVisual();
        };
    }

    bool WantsFrames => Moving || Continuous && Theme.Animate;

    double Settle => Math.Max(1e-9, Math.Abs(Range) * 0.001);

    bool Moving => _target.Any(kv => Math.Abs(_shown[kv.Key] - kv.Value) > Settle);

    /// <summary>Sets a reading. The first reading (or every reading with animations off) shows at once.</summary>
    public void Set(string key, double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
            return;
        _target[key] = v;
        if (!_shown.ContainsKey(key) || !Theme.Animate || Motion.Paused)
            _shown[key] = v;
        Wake(); // moving toward the new value, or an always-animating gauge that just got data
        InvalidateVisual();
    }

    /// <summary>Starts frame callbacks if this gauge wants them (e.g. a peak-hold segment is waiting).</summary>
    protected void Wake()
    {
        if (WantsFrames) Hook();
    }

    protected bool Has(string key) => _shown.ContainsKey(key);
    protected double V(string key, double fallback = 0) => _shown.TryGetValue(key, out var v) ? v : fallback;

    void Hook()
    {
        if (!IsLoaded || !Live.Add(this))
            return;
        _lastTick = Now;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / 30.0) };
            _timer.Tick += (_, _) =>
            {
                foreach (var g in Live.ToArray())
                    g.OnFrame();
            };
            if (!Motion.Paused)
                _timer.Start();
        }
    }

    /// <summary>Shows every reading at its target at once (after a pause).</summary>
    void Snap()
    {
        foreach (var key in _target.Keys.ToArray())
            _shown[key] = _target[key];
        _lastTick = Now;
        InvalidateVisual();
    }

    /// <summary>Stops frame callbacks (also called when the tile is thrown away).</summary>
    public void Unhook()
    {
        if (!Live.Remove(this))
            return;
        if (Live.Count == 0 && _timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    void OnFrame()
    {
        double now = Now, dt = now - _lastTick, settle = Settle;
        _lastTick = now;
        double k = 1 - Math.Exp(-Speed * Math.Min(dt, 0.25));
        foreach (var key in _target.Keys.ToArray())
        {
            double target = _target[key], next = _shown[key] + (target - _shown[key]) * k;
            _shown[key] = Math.Abs(target - next) <= settle ? target : next; // close enough: snap and stop
        }
        if (IsVisible)
            InvalidateVisual();
        if (!IsVisible || !WantsFrames)
            Unhook();
    }

    // ---------------------------------------------------------------- drawing helpers

    protected enum Align { Left, Center, Right }

    protected Size Text(DrawingContext dc, string s, double x, double y, double size, Brush brush, FontFamily font,
                        Align align = Align.Center, FontWeight? weight = null, bool slant = false)
    {
        if (size < 4 || string.IsNullOrEmpty(s))
            return default;
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double left = align switch { Align.Left => x, Align.Right => x - ft.Width, _ => x - ft.Width / 2 };
        double top = y - ft.Height / 2;
        if (slant)
            dc.PushTransform(new SkewTransform(-10, 0, x, y));
        dc.DrawText(ft, new Point(left, top));
        if (slant)
            dc.Pop();
        return new Size(ft.Width, ft.Height);
    }

    protected static Point Polar(Point c, double r, double deg) =>
        new(c.X + Math.Cos(deg * Math.PI / 180) * r, c.Y + Math.Sin(deg * Math.PI / 180) * r);

    /// <summary>Arc from a0 to a1 (degrees, clockwise from 3 o'clock).</summary>
    protected static Geometry Arc(Point c, double r, double a0, double a1)
    {
        if (a1 - a0 >= 359.9)
            return new EllipseGeometry(c, r, r);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(c, r, a0), false, false);
            ctx.ArcTo(Polar(c, r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>A frozen pen, shared between frames and gauges (widths are rounded to 1/100 DIP for reuse).</summary>
    protected static Pen MakePen(Brush b, double width, PenLineCap cap = PenLineCap.Flat)
    {
        var key = (b, Math.Round(width, 2), cap);
        if (Pens.TryGetValue(key, out var p))
            return p;
        if (Pens.Count > 2048)
            Pens.Clear(); // widths follow tile sizes; don't let resizes and theme changes pile up
        p = new Pen(b, key.Item2) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        p.Freeze();
        Pens[key] = p;
        return p;
    }

    protected Brush Alpha(Brush b, byte a)
    {
        if (b is not SolidColorBrush s)
            return b;
        if (!_alpha.TryGetValue((s.Color, a), out var r))
            _alpha[(s.Color, a)] = r = Theme.Freeze(new SolidColorBrush(Theme.WithAlpha(s.Color, a)));
        return r;
    }

    /// <summary>Strokes a geometry, with a soft halo first when the theme glows.</summary>
    protected void Stroke(DrawingContext dc, Geometry g, Brush b, double width, PenLineCap cap = PenLineCap.Flat)
    {
        if (P.Glow)
            dc.DrawGeometry(null, MakePen(Alpha(b, 0x40), width + 7, cap), g);
        dc.DrawGeometry(null, MakePen(b, width, cap), g);
    }

    protected void Dot(DrawingContext dc, Point p, double r, Brush b)
    {
        if (P.Glow)
            dc.DrawEllipse(Alpha(b, 0x40), null, p, r + 4, r + 4);
        dc.DrawEllipse(b, null, p, r, r);
    }

    protected static string Dash => "–";
}

/// <summary>Electric-car style power meter: 270° arc that turns amber / red past the limits, with a tick ring.</summary>
public sealed class ArcGauge : Gauge
{
    public double Max = 100, WarnAt = 75, CritAt = 90;
    public string Label = "", Unit = "%";
    public Func<double, string> Format = v => v.ToString("0");
    protected override double Range => Max;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var c = new Point(w / 2, h * 0.55);
        double r = Math.Min(w * 0.38, h * 0.42);
        if (r < 8) return;
        double thick = P.Thin ? Math.Max(2, r * 0.035) : r * 0.11;

        dc.DrawGeometry(null, MakePen(P.Track, thick, PenLineCap.Round), Arc(c, r, 135, 405));

        double v = V("v"), f = Math.Clamp(v / Max, 0, 1);
        if (Has("v") && f > 0.003)
        {
            // Up to three colour runs: normal → warning → critical.
            double wf = Math.Clamp(WarnAt / Max, 0, 1), cf = Math.Clamp(CritAt / Max, 0, 1);
            foreach (var (from, to, brush) in new[] { (0.0, wf, P.Accent), (wf, cf, P.Warn), (cf, 1.0, P.Crit) })
            {
                if (f <= from) break;
                double end = Math.Min(f, to);
                if (end > from)
                    Stroke(dc, Arc(c, r, 135 + 270 * from, 135 + 270 * end), brush, thick, PenLineCap.Round);
            }
            Dot(dc, Polar(c, r, 135 + 270 * f), thick * 0.38 + 1.5, P.Fg);
        }

        Pen majorTick = MakePen(P.Fg, 1.4), minorTick = MakePen(P.Dim, 1);
        for (int i = 0; i <= 50; i++)
        {
            double a = 135 + 270 * i / 50.0, r1 = r + thick * 0.9 + 3, r2 = r1 + (i % 10 == 0 ? 7 : 3.5);
            dc.DrawLine(i % 10 == 0 ? majorTick : minorTick, Polar(c, r1, a), Polar(c, r2, a));
        }

        Text(dc, Has("v") ? Format(v) : Dash, c.X, c.Y - r * 0.04, r * 0.5, P.Fg, P.Display,
             weight: P.Thin ? FontWeights.Light : null, slant: P.Italic);
        Text(dc, string.IsNullOrEmpty(Unit) ? Label : $"{Unit} · {Label}", c.X, c.Y + r * 0.38, Math.Max(9, r * 0.13), P.Dim, P.Mono);
    }
}

/// <summary>Analogue dial with a tapered needle that eases into place, numbers and a redline.</summary>
public sealed class Speedometer : Gauge
{
    public double Max = 180, Major = 20, Minor = 5;
    public double? Redline;
    public bool RedlineIsLow;
    public string Label = "";
    public Func<double, string> Format = v => v.ToString("0");
    public Func<double, string> TickFormat = v => v.ToString("0");
    protected override double Speed => 4;
    protected override double Range => Max;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var c = new Point(w / 2, h * 0.53);
        double r = Math.Min(w * 0.44, h * 0.45);
        if (r < 10) return;
        double A(double x) => 135 + 270 * Math.Clamp(x / Max, 0, 1);

        var face = new RadialGradientBrush(Theme.WithAlpha(Colors.White, 0x12), Theme.WithAlpha(Colors.White, 0))
        { GradientOrigin = new Point(0.5, 0.3), Center = new Point(0.5, 0.45), RadiusX = 0.6, RadiusY = 0.6 };
        face.Freeze();
        dc.DrawEllipse(face, MakePen(P.Track, 2), c, r, r);

        if (Redline is double red)
        {
            var zone = RedlineIsLow ? Arc(c, r * 0.9, A(0), A(red)) : Arc(c, r * 0.9, A(red), A(Max));
            dc.DrawGeometry(null, MakePen(Alpha(P.Crit, 0xD0), r * 0.07), zone);
        }

        for (double x = 0; x <= Max + 1e-6; x += Minor)
        {
            bool major = Math.Abs(x / Major - Math.Round(x / Major)) < 1e-6;
            bool inRed = Redline is double rl && (RedlineIsLow ? x <= rl : x >= rl);
            double a = A(x), r2 = r * 0.94, r1 = r2 - (major ? r * 0.12 : r * 0.05);
            dc.DrawLine(MakePen(inRed ? P.Crit : major ? P.Fg : P.Dim, major ? 2 : 1), Polar(c, r1, a), Polar(c, r2, a));
            if (major)
            {
                var p = Polar(c, r * 0.7, a);
                Text(dc, TickFormat(x), p.X, p.Y, r * 0.11, P.Fg, P.Display, slant: P.Italic);
            }
        }

        if (Has("v"))
        {
            double a = A(V("v"));
            var needle = new StreamGeometry();
            using (var ctx = needle.Open())
            {
                ctx.BeginFigure(Polar(c, r * 0.9, a), true, true);
                ctx.LineTo(Polar(c, r * 0.035, a + 90), false, false);
                ctx.LineTo(Polar(c, r * 0.14, a + 180), false, false);
                ctx.LineTo(Polar(c, r * 0.035, a - 90), false, false);
            }
            needle.Freeze();
            if (P.Glow)
                dc.DrawGeometry(null, MakePen(Alpha(P.Accent, 0x50), 6), needle);
            dc.DrawGeometry(P.Accent, null, needle);
        }
        dc.DrawEllipse(P.Face, MakePen(P.Dim, 1.5), c, r * 0.09, r * 0.09);

        Text(dc, Has("v") ? Format(V("v")) : Dash, c.X, c.Y + r * 0.5, r * 0.27, P.Fg, P.Display, slant: P.Italic);
        Text(dc, Label, c.X, c.Y + r * 0.74, Math.Max(9, r * 0.09), P.Dim, P.Mono);
    }
}

/// <summary>Race-car shift lights: discrete segments along an arc, with a peak-hold segment that lingers.</summary>
public sealed class LedArc : Gauge
{
    public double Max = 100, WarnAt = 65, CritAt = 85;
    public string Label = "";
    public Func<double, string> Format = v => v.ToString("0");
    protected override double Speed => 10;
    protected override double Range => Max;

    int _peak, _lit;
    double _peakAt;
    // Keep drawing while a peak-hold segment is waiting to fade.
    protected override bool Continuous => _peak > _lit;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var c = new Point(w / 2, h * 0.58);
        double r = Math.Min(w * 0.38, h * 0.44);
        if (r < 10) return;
        const int n = 26;
        double v = V("v"), f = Math.Clamp(v / Max, 0, 1);
        int lit = (int)Math.Round(n * f);
        if (lit >= _peak || Now - _peakAt > 1.5)
        {
            _peak = lit;
            _peakAt = Now;
        }
        _lit = lit;
        Wake();

        double thick = r * 0.2, gap = 1.3;
        for (int i = 0; i < n; i++)
        {
            double fr = (i + 1.0) / n, x = fr * Max;
            bool on = Has("v") && (i < lit || i == _peak - 1);
            var brush = x > CritAt ? P.Crit : x > WarnAt ? P.Warn : P.Accent;
            var seg = Arc(c, r, 150 + 240.0 * i / n + gap, 150 + 240.0 * (i + 1) / n - gap);
            if (on) Stroke(dc, seg, brush, thick);
            else dc.DrawGeometry(null, MakePen(P.Track, thick), seg);
        }
        Text(dc, Has("v") ? Format(v) : Dash, c.X, c.Y + r * 0.04, r * 0.55, P.Fg, P.Display, slant: P.Italic);
        Text(dc, Label, c.X, c.Y + r * 0.5, Math.Max(9, r * 0.14), P.Dim, P.Mono);
    }
}

/// <summary>Concentric rings (up to three readings in percent), with a legend on the right.</summary>
public sealed class RingsGauge : Gauge
{
    public readonly List<(string Label, Brush Brush)> Items = new();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var c = new Point(Math.Min(w * 0.38, h * 0.5 + 6), h / 2);
        double R = Math.Min(w * 0.32, h * 0.43), th = R * 0.17;
        if (R < 10) return;
        for (int i = 0; i < Items.Count; i++)
        {
            var (label, brush) = Items[i];
            double r = R - i * th * 1.35, v = Math.Clamp(V("i" + i) / 100, 0, 1);
            if (r <= th) break;
            dc.DrawEllipse(null, MakePen(P.Track, th), c, r, r);
            if (Has("i" + i) && v > 0.004)
                Stroke(dc, Arc(c, r, -90, -90 + 360 * v), brush, th, PenLineCap.Round);
            double ly = c.Y - R * 0.55 + i * R * 0.55, lx = c.X + R + 14;
            Text(dc, label, lx, ly - R * 0.1, Math.Max(9, R * 0.14), P.Dim, P.Mono, Align.Left);
            Text(dc, Has("i" + i) ? $"{V("i" + i):0}%" : Dash, lx, ly + R * 0.13, Math.Max(12, R * 0.24), brush, P.Display, Align.Left);
        }
    }
}

/// <summary>
/// Airliner-style moving tape: the scale scrolls past a fixed pointer, coloured zones run down the side and a
/// trend line shows where the value will be in about 6 seconds.
/// </summary>
public sealed class TapeGauge : Gauge
{
    public double WarnAt = 70, CritAt = 85, Span = 26, Step = 1, LabelEvery = 5;
    /// <summary>Low values are the bad ones (FPS).</summary>
    public bool LowIsBad;
    public string Unit = "", Label = "";
    public Func<double, string> Format = v => v.ToString("0");
    readonly Queue<(double T, double V)> _history = new();
    protected override double Range => Span;

    public void Push(double? value)
    {
        Set("v", value);
        if (value is double v)
        {
            _history.Enqueue((Now, v));
            while (_history.Count > 0 && Now - _history.Peek().T > 6)
                _history.Dequeue();
        }
    }

    Brush Zone(double x) => LowIsBad
        ? x <= CritAt ? P.Crit : x <= WarnAt ? P.Warn : P.Good
        : x >= CritAt ? P.Crit : x >= WarnAt ? P.Warn : P.Good;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double v = V("v"), cy = h / 2, k = h / Span, x1 = w * 0.62;
        double step = Step * Math.Max(1, Math.Ceiling(12 / (k * Step))); // keep ticks ≥12 px apart

        dc.PushClip(new RectangleGeometry(new Rect(0, 2, w, h - 4)));
        for (double x = Math.Floor((v - Span / 2) / step) * step; x <= v + Span / 2; x += step)
        {
            double y = cy - (x - v) * k;
            dc.DrawRectangle(Alpha((SolidColorBrush)Zone(x), 0xB0), null, new Rect(x1 + 2, y - k * step / 2, 4, k * step));
            bool major = Math.Abs(x / LabelEvery - Math.Round(x / LabelEvery)) < 1e-6;
            dc.DrawLine(MakePen(major ? P.Fg : P.Dim, 1), new Point(x1 - (major ? 12 : 6), y), new Point(x1, y));
            if (major)
                Text(dc, x.ToString("0"), x1 - 16, y, Math.Max(10, Math.Min(14, k * 4)), P.Dim, P.Mono, Align.Right);
        }
        if (_history.Count > 1)
        {
            var (t0, v0) = _history.Peek();
            double trend = (v - v0) / Math.Max(1, Now - t0) * 6;
            dc.DrawLine(MakePen(P.Accent2, 2.5), new Point(x1 + 12, cy), new Point(x1 + 12, Math.Clamp(cy - trend * k, 4, h - 4)));
        }
        dc.Pop();

        double bw = Math.Max(16, Math.Min(w * 0.4, x1 - 26)), bh = Math.Min(30, Math.Max(18, h * 0.14)), bx = Math.Max(2, x1 - bw - 22);
        var col = Has("v") ? Zone(v) == P.Good ? P.Fg : Zone(v) : P.Dim;
        var box = new StreamGeometry();
        using (var ctx = box.Open())
        {
            ctx.BeginFigure(new Point(bx, cy - bh / 2), true, true);
            ctx.LineTo(new Point(bx + bw, cy - bh / 2), true, false);
            ctx.LineTo(new Point(bx + bw + 9, cy), true, false);
            ctx.LineTo(new Point(bx + bw, cy + bh / 2), true, false);
            ctx.LineTo(new Point(bx, cy + bh / 2), true, false);
        }
        box.Freeze();
        dc.DrawGeometry(P.Face, MakePen(col, 1.5), box);
        Text(dc, Has("v") ? Format(v) : Dash, bx + bw / 2 + 2, cy, bh * 0.62, col, P.Mono);
        Text(dc, Unit, w - 3, h - 12, 11, P.Dim, P.Mono, Align.Right);
        if (!string.IsNullOrEmpty(Label))
            Text(dc, Label, 6, 12, 11, P.Dim, P.Mono, Align.Left);
    }
}

/// <summary>Liquid-filled tubes for temperatures, with a gently moving surface.</summary>
public sealed class TubesGauge : Gauge
{
    public readonly List<string> Labels = new();
    public double Min = 20, Max = 100;
    // Only animate the liquid once there's something to show (no sensors without admin is common).
    protected override bool Continuous => Labels.Count > 0 && Has("i0");
    protected override double Speed => 3;
    protected override double Range => Max - Min;

    protected override void OnRender(DrawingContext dc)
    {
        int n = Labels.Count;
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w < 30 || h < 50) return;
        double tw = Math.Min(w / (n * 2 + 1), 30), gap = (w - tw * n) / (n + 1), top = h * 0.16, bottom = h - 22, H = bottom - top;
        for (int i = 0; i < n; i++)
        {
            double v = V("i" + i), x = gap + i * (tw + gap), f = Math.Clamp((v - Min) / (Max - Min), 0, 1), y = bottom - H * f;
            var col = v >= Theme.TempCrit ? P.Crit : v >= Theme.TempWarn ? P.Warn : P.Accent;
            var tube = new RectangleGeometry(new Rect(x, top, tw, H), tw / 2, tw / 2);
            dc.DrawGeometry(P.Track, null, tube);
            if (Has("i" + i))
            {
                dc.PushClip(tube);
                var liquid = new StreamGeometry();
                using (var ctx = liquid.Open())
                {
                    ctx.BeginFigure(new Point(x, bottom), true, true);
                    for (double px = 0; px <= tw; px += 2)
                        ctx.LineTo(new Point(x + px, y + Math.Sin(px * 0.45 + Now * 4 + i) * 1.6), false, false);
                    ctx.LineTo(new Point(x + tw, bottom), false, false);
                }
                liquid.Freeze();
                var fill = new LinearGradientBrush(((SolidColorBrush)col).Color, Theme.WithAlpha(((SolidColorBrush)col).Color, 0x40), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, liquid);
                dc.DrawRectangle(Alpha(Brushes.White, 0x2A), null, new Rect(x + tw * 0.18, top + tw * 0.4, tw * 0.14, Math.Max(0, H - tw * 0.8)));
                dc.Pop();
            }
            dc.DrawGeometry(null, MakePen(P.Dim, 1), tube);
            Text(dc, Has("i" + i) ? W.Temp(v) : Dash, x + tw / 2, top - 10, Math.Max(10, Math.Min(15, tw * 0.55)), col, P.Display, slant: P.Italic);
            Text(dc, Labels[i], x + tw / 2, bottom + 11, Math.Max(9, Math.Min(12, tw * 0.42)), P.Dim, P.Mono);
        }
    }
}

/// <summary>Every CPU thread as a honeycomb cell that fills with load.</summary>
public sealed class HexGrid : Gauge
{
    int _count;

    public void SetLoads(IReadOnlyList<double> loads)
    {
        _count = loads.Count;
        for (int i = 0; i < loads.Count; i++)
            Set("t" + i, loads[i]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, s3 = Math.Sqrt(3);
        if (_count == 0 || w < 30 || h < 30) return;

        // Pick the column count that gives the biggest cells for this tile's shape.
        int bestCols = 1;
        double r = 0;
        for (int cols = 1; cols <= _count; cols++)
        {
            int rows = (int)Math.Ceiling(_count / (double)cols);
            double rr = Math.Min((w - 4) / (cols * s3 + (rows > 1 ? s3 / 2 : 0)), (h - 4) / (rows * 1.5 + 0.5));
            if (rr > r) { r = rr; bestCols = cols; }
        }
        int rowsN = (int)Math.Ceiling(_count / (double)bestCols);
        double ox = (w - (bestCols * s3 + (rowsN > 1 ? s3 / 2 : 0)) * r) / 2 + s3 * r / 2, oy = (h - (rowsN * 1.5 + 0.5) * r) / 2 + r;

        for (int i = 0; i < _count; i++)
        {
            int row = i / bestCols, col = i % bestCols;
            var c = new Point(ox + col * s3 * r + (row % 2) * s3 * r / 2, oy + row * 1.5 * r);
            double v = Math.Clamp(V("t" + i) / 100, 0, 1);
            var hex = new StreamGeometry();
            using (var ctx = hex.Open())
            {
                ctx.BeginFigure(Polar(c, r * 0.92, -90), true, true);
                for (int k = 1; k < 6; k++)
                    ctx.LineTo(Polar(c, r * 0.92, -90 + 60 * k), true, false);
            }
            hex.Freeze();
            var heat = Theme.ForPercent(v * 100);
            dc.DrawGeometry(P.Track, null, hex);
            dc.PushClip(hex);
            dc.PushOpacity(0.3 + v * 0.7);
            dc.DrawRectangle(heat, null, new Rect(c.X - r, c.Y + r - 2 * r * v, 2 * r, 2 * r * v));
            dc.Pop();
            dc.Pop();
            dc.DrawGeometry(null, MakePen(v > 0.75 ? heat : P.Dim, 1), hex);
            if (r > 14)
                Text(dc, (v * 100).ToString("0"), c.X, c.Y, r * 0.42, P.Fg, P.Mono);
        }
    }
}

/// <summary>Sweeping scope; the busiest processes sit closest to the centre and fade between sweeps.</summary>
public sealed class RadarGauge : Gauge
{
    readonly List<(string Name, double Cpu)> _blips = new();
    protected override bool Continuous => true;

    public void SetBlips(IEnumerable<(string Name, double Cpu)> blips)
    {
        _blips.Clear();
        _blips.AddRange(blips.Take(8));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, R = Math.Min(w, h) * 0.44;
        if (R < 15) return;
        var c = new Point(w / 2, h / 2);
        double sweep = Theme.Animate ? Now * 75 % 360 : 300;

        for (int i = 1; i <= 3; i++)
            dc.DrawEllipse(null, MakePen(P.Track, 1), c, R * i / 3, R * i / 3);
        dc.DrawLine(MakePen(P.Track, 1), new Point(c.X - R, c.Y), new Point(c.X + R, c.Y));
        dc.DrawLine(MakePen(P.Track, 1), new Point(c.X, c.Y - R), new Point(c.X, c.Y + R));

        // Fading wedge behind the beam.
        for (int i = 0; i < 12; i++)
        {
            var wedge = new StreamGeometry();
            using (var ctx = wedge.Open())
            {
                ctx.BeginFigure(c, true, true);
                ctx.LineTo(Polar(c, R, sweep - (i + 1) * 4), false, false);
                ctx.ArcTo(Polar(c, R, sweep - i * 4), new Size(R, R), 0, false, SweepDirection.Clockwise, false, false);
            }
            wedge.Freeze();
            dc.DrawGeometry(Alpha(P.Accent, (byte)(0x38 * (1 - i / 12.0))), null, wedge);
        }
        Stroke(dc, new LineGeometry(c, Polar(c, R, sweep)), P.Accent, 1.5);

        for (int i = 0; i < _blips.Count; i++)
        {
            var (name, cpu) = _blips[i];
            double a = (i * 61 + 30) % 360, d = R * (1 - Math.Clamp(cpu / 50, 0.05, 0.85));
            double since = ((sweep - a) % 360 + 360) % 360, alpha = Theme.Animate ? Math.Max(0.15, 1 - since / 230) : 1;
            var p = Polar(c, d, a);
            var col = cpu > 30 ? P.Warn : P.Accent;
            dc.PushOpacity(alpha);
            Dot(dc, p, 2.5 + Math.Min(cpu, 60) / 15, col);
            if (alpha > 0.35)
                Text(dc, $"{name} {cpu:0}%", p.X + 7, p.Y - 8, Math.Max(9, Math.Min(13, R * 0.085)), P.Fg, P.Mono, Align.Left);
            dc.Pop();
        }
    }
}

/// <summary>Oscilloscope: phosphor trace of one or two history series on a measured graticule.</summary>
public sealed class ScopeGauge : Gauge
{
    public string SeriesA = "net.down";
    public string? SeriesB;
    public string Caption = "";
    public Func<double, string> Format = v => v.ToString("0");
    public double? FixedMax;
    const int Points = 120;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, pad = 6;
        if (w < 40 || h < 30) return;
        var area = new Rect(pad, pad, w - pad * 2, h - pad * 2);
        var grid = MakePen(P.Track, 1);
        for (int i = 0; i <= 10; i++) dc.DrawLine(grid, new Point(area.X + area.Width * i / 10, area.Y), new Point(area.X + area.Width * i / 10, area.Bottom));
        for (int i = 0; i <= 6; i++) dc.DrawLine(grid, new Point(area.X, area.Y + area.Height * i / 6), new Point(area.Right, area.Y + area.Height * i / 6));
        var mid = area.Y + area.Height / 2;
        for (int i = 0; i <= 50; i++) dc.DrawLine(MakePen(P.Dim, 1), new Point(area.X + area.Width * i / 50, mid - 2), new Point(area.X + area.Width * i / 50, mid + 2));

        var a = Sensors.HistoryStore.Shared.Recent(SeriesA, Points);
        var b = SeriesB == null ? Array.Empty<double>() : Sensors.HistoryStore.Shared.Recent(SeriesB, Points);
        double max = FixedMax ?? Math.Max(1, Math.Max(a.DefaultIfEmpty(0).Max(), b.DefaultIfEmpty(0).Max()) * 1.2);
        Trace(dc, b, area, max, P.Accent2);
        Trace(dc, a, area, max, P.Accent);
        if (a.Length > 0)
            Text(dc, Format(a[^1]), area.Right - 4, area.Y + 12, 13, P.Accent, P.Mono, Align.Right);
        if (Caption.Length > 0)
            Text(dc, Caption, area.X + 4, area.Bottom - 9, 10, P.Dim, P.Mono, Align.Left);
    }

    void Trace(DrawingContext dc, double[] data, Rect area, double max, Brush brush)
    {
        if (data.Length < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double step = area.Width / (Points - 1), x = area.Right - (data.Length - 1) * step;
            ctx.BeginFigure(new Point(x, area.Bottom - Math.Clamp(data[0] / max, 0, 1) * area.Height), false, false);
            for (int i = 1; i < data.Length; i++)
                ctx.LineTo(new Point(x + i * step, area.Bottom - Math.Clamp(data[i] / max, 0, 1) * area.Height), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, MakePen(Alpha(brush, 0x45), 5), g);
        dc.DrawGeometry(null, MakePen(brush, 1.4), g);
    }
}

/// <summary>Seven-segment LED digits, including the faint unlit segments. Supports 0-9, '-', '.', ':' and spaces.</summary>
public sealed class SevenSegment : Gauge
{
    static readonly Dictionary<char, string> Segs = new()
    {
        ['0'] = "abcdef", ['1'] = "bc", ['2'] = "abged", ['3'] = "abgcd", ['4'] = "fgbc", ['5'] = "afgcd",
        ['6'] = "afgedc", ['7'] = "abc", ['8'] = "abcdefg", ['9'] = "abcdfg", ['-'] = "g", [' '] = "",
    };

    string _text = "", _label = "";
    Brush? _color;

    public string Label
    {
        get => _label;
        set { if (value != _label) { _label = value; InvalidateVisual(); } }
    }

    /// <summary>Digit colour (null = the theme accent); e.g. red past a temperature limit.</summary>
    public Brush? Color
    {
        get => _color;
        set { if (!ReferenceEquals(value, _color)) { _color = value; InvalidateVisual(); } }
    }

    public string Value
    {
        get => _text;
        set
        {
            if (value == _text) return;
            _text = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20 || _text.Length == 0) return;
        int digits = _text.Count(ch => ch != '.' && ch != ':'), colons = _text.Count(ch => ch == ':');
        double labelH = Label.Length > 0 ? 18 : 0;
        double hgt = Math.Min((h - labelH) * 0.78, (w - 16) / (digits * 0.69 + colons * 0.3 + 0.1));
        double dw = hgt * 0.56, t = hgt * 0.12, g = t * 0.2;
        double total = digits * (dw + t * 1.1) + colons * hgt * 0.3, x = (w - total) / 2, y = (h - labelH - hgt) / 2;
        var on = Color ?? P.Accent;
        var off = Alpha(on, 0x16);

        dc.PushTransform(new SkewTransform(-6, 0, w / 2, y + hgt / 2));
        foreach (char ch in _text)
        {
            if (ch == '.')
            {
                Dot(dc, new Point(x - dw * 0.12, y + hgt - t / 2), t * 0.55, on);
                continue;
            }
            if (ch == ':')
            {
                bool blink = !Theme.Animate || DateTime.Now.Second % 2 == 0;
                Dot(dc, new Point(x + hgt * 0.12, y + hgt * 0.3), t * 0.55, blink ? on : off);
                Dot(dc, new Point(x + hgt * 0.12, y + hgt * 0.7), t * 0.55, blink ? on : off);
                x += hgt * 0.3;
                continue;
            }
            double L = x + t / 2, R = x + dw - t / 2, T = y + t / 2, M = y + hgt / 2, B = y + hgt - t / 2;
            Segs.TryGetValue(ch, out var lit);
            lit ??= "";
            foreach (char s in "abcdefg")
            {
                var geo = s switch
                {
                    'a' => H(L + g, T, R - g, t), 'g' => H(L + g, M, R - g, t), 'd' => H(L + g, B, R - g, t),
                    'f' => Vt(L, T + g, M - g, t), 'b' => Vt(R, T + g, M - g, t), 'e' => Vt(L, M + g, B - g, t), _ => Vt(R, M + g, B - g, t),
                };
                if (lit.Contains(s))
                {
                    if (P.Glow) dc.DrawGeometry(null, MakePen(Alpha(on, 0x40), 4), geo);
                    dc.DrawGeometry(on, null, geo);
                }
                else
                {
                    dc.DrawGeometry(off, null, geo);
                }
            }
            x += dw + t * 1.1;
        }
        dc.Pop();
        if (Label.Length > 0)
            Text(dc, Label, w / 2, h - 10, 11, P.Dim, P.Mono);
    }

    static Geometry H(double x1, double y, double x2, double t) => Poly((x1, y), (x1 + t / 2, y - t / 2), (x2 - t / 2, y - t / 2), (x2, y), (x2 - t / 2, y + t / 2), (x1 + t / 2, y + t / 2));
    static Geometry Vt(double x, double y1, double y2, double t) => Poly((x, y1), (x + t / 2, y1 + t / 2), (x + t / 2, y2 - t / 2), (x, y2), (x - t / 2, y2 - t / 2), (x - t / 2, y1 + t / 2));

    static Geometry Poly(params (double X, double Y)[] p)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(p[0].X, p[0].Y), true, true);
            for (int i = 1; i < p.Length; i++)
                ctx.LineTo(new Point(p[i].X, p[i].Y), false, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>The PC as a little solar system: each part orbits faster and grows as it works harder.</summary>
public sealed class OrbitGauge : Gauge
{
    public readonly List<(string Label, Brush Brush, string Unit)> Bodies = new();
    readonly List<double> _angles = new();
    double _last = Now;
    protected override bool Continuous => true;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;
        var c = new Point(w / 2, h * 0.52);
        double R = Math.Min(w * 0.48, h * 1.05), dt = Math.Min(0.25, Now - _last);
        _last = Now;
        while (_angles.Count < Bodies.Count)
            _angles.Add(_angles.Count * 97.0);

        var sun = new RadialGradientBrush(Colors.White, Theme.WithAlpha(Theme.Accent2Color, 0));
        sun.GradientStops.Insert(1, new GradientStop(Theme.Accent2Color, 0.35));
        sun.Freeze();
        dc.DrawEllipse(sun, null, c, R * 0.12, R * 0.12);

        for (int i = 0; i < Bodies.Count; i++)
        {
            var (label, brush, unit) = Bodies[i];
            double k = 0.3 + 0.62 * i / Math.Max(1, Bodies.Count - 1), rx = R * k * 0.5, ry = rx * 0.42;
            double v = Math.Clamp(V("i" + i), 0, 100);
            if (Theme.Animate)
                _angles[i] += (8 + v * 0.9) * (1.2 - k * 0.5) * dt;
            dc.DrawEllipse(null, MakePen(P.Track, 1), c, rx, ry);
            for (int j = 12; j >= 1; j--)
            {
                double a = (_angles[i] - j * 2.6) * Math.PI / 180;
                dc.DrawEllipse(Alpha(brush, (byte)(0x70 * (1 - j / 13.0))), null, new Point(c.X + Math.Cos(a) * rx, c.Y + Math.Sin(a) * ry), 1.3, 1.3);
            }
            double ang = _angles[i] * Math.PI / 180, pr = 3 + v / 100 * 6;
            var p = new Point(c.X + Math.Cos(ang) * rx, c.Y + Math.Sin(ang) * ry);
            Dot(dc, p, pr, brush);
            Text(dc, Has("i" + i) ? $"{label} {v:0}{unit}" : label, p.X + pr + 4, p.Y - pr - 4, Math.Max(9, Math.Min(13, R * 0.045)), P.Fg, P.Display, Align.Left);
        }
    }
}

/// <summary>
/// Caution / warning lamp panel with master buttons that blink while anything is lit. A tap acknowledges the
/// current cautions: the lamps stay lit but stop blinking until the set of cautions changes.
/// </summary>
public sealed class Annunciator : Gauge
{
    readonly List<(string Label, int State)> _lamps = new();
    readonly DispatcherTimer _blink = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(400) };
    bool _blinkOn = true;
    string _alarms = "", _acknowledged = "";

    public Annunciator()
    {
        // Only toggles (and redraws) 2.5 times a second while an unacknowledged caution / warning is lit.
        // Paused motion stops it here; the next reading (SetLamps) starts it again.
        _blink.Tick += (_, _) =>
        {
            if (Motion.Paused)
            {
                UpdateBlink();
                return;
            }
            _blinkOn = !_blinkOn;
            InvalidateVisual();
        };
        Loaded += (_, _) => UpdateBlink();
        Unloaded += (_, _) => _blink.Stop();
        IsVisibleChanged += (_, _) => UpdateBlink();
    }

    bool Blinking => _alarms.Length > 0 && _alarms != _acknowledged && Theme.Animate && !Motion.Paused;

    /// <summary>State: 0 off, 1 caution (amber), 2 warning (red), 3 information (steady, accent colour, no master light).</summary>
    public void SetLamps(IEnumerable<(string Label, int State)> lamps)
    {
        _lamps.Clear();
        _lamps.AddRange(lamps);
        _alarms = string.Join("|", _lamps.Where(l => l.State is 1 or 2).Select(l => $"{l.Label}:{l.State}"));
        if (_alarms != _acknowledged)
            _acknowledged = ""; // something new (or cleared): blink again next time
        UpdateBlink();
        InvalidateVisual();
    }

    /// <summary>Stops the master lights blinking for the cautions lit right now.</summary>
    public void Acknowledge()
    {
        _acknowledged = _alarms;
        UpdateBlink();
        InvalidateVisual();
    }

    void UpdateBlink()
    {
        if (Blinking && IsLoaded && IsVisible)
        {
            if (!_blink.IsEnabled)
                _blink.Start();
        }
        else
        {
            _blink.Stop();
            _blinkOn = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 50 || _lamps.Count == 0) return;
        bool warn = _lamps.Any(l => l.State == 2), caution = _lamps.Any(l => l.State == 1);
        bool blinkOn = !Blinking || _blinkOn;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole panel takes the acknowledge tap
        var dark = Alpha(Brushes.Black, 0x80);
        var off = ((SolidColorBrush)Theme.Background).Color.R > 0x90 ? Alpha(Brushes.Black, 0x10) : Alpha(Brushes.White, 0x08);

        double bh = Math.Max(20, Math.Min(34, h * 0.15)), bw = (w - 10) / 2;
        Master(dc, new Rect(0, 0, bw, bh), "MASTER WARN", warn && blinkOn, P.Crit, off);
        Master(dc, new Rect(bw + 10, 0, bw, bh), "MASTER CAUTION", caution && blinkOn, P.Warn, off);

        int cols = w > h * 1.6 ? 4 : 2, rows = (int)Math.Ceiling(_lamps.Count / (double)cols);
        double top = bh + 8, lw = (w - (cols - 1) * 8) / cols, lh = (h - top - (rows - 1) * 6) / rows;
        for (int i = 0; i < _lamps.Count; i++)
        {
            var (label, state) = _lamps[i];
            var r = new Rect((i % cols) * (lw + 8), top + i / cols * (lh + 6), lw, lh);
            var col = state == 2 ? P.Crit : state == 3 ? P.Accent : P.Warn;
            if (state > 0)
            {
                if (P.Glow) dc.DrawRoundedRectangle(Alpha(col, 0x40), null, new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), 4, 4);
                dc.DrawRoundedRectangle(col, null, r, 2, 2);
            }
            else
            {
                dc.DrawRoundedRectangle(off, MakePen(P.Track, 1), r, 2, 2);
            }
            Text(dc, label, r.X + r.Width / 2, r.Y + r.Height / 2, Math.Max(9, Math.Min(r.Height * 0.38, r.Width * 0.13)),
                 state > 0 ? dark : P.Dim, P.Display, weight: FontWeights.SemiBold);
        }
    }

    void Master(DrawingContext dc, Rect r, string label, bool lit, Brush col, Brush off)
    {
        if (lit && P.Glow) dc.DrawRoundedRectangle(Alpha(col, 0x40), null, new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), 5, 5);
        dc.DrawRoundedRectangle(lit ? col : off, MakePen(lit ? col : P.Track, 1), r, 3, 3);
        Text(dc, label, r.X + r.Width / 2, r.Y + r.Height / 2, Math.Max(9, Math.Min(r.Height * 0.4, r.Width * 0.09)),
             lit ? Alpha(Brushes.Black, 0x90) : P.Dim, P.Display, weight: FontWeights.SemiBold);
    }
}

/// <summary>Ensō: an ink brush circle that closes as the value rises, with bristle texture and a vermilion seal.</summary>
public sealed class EnsoGauge : Gauge
{
    public double Max = 100;
    public string Seal = "負荷"; // two characters stacked in the seal
    public Func<double, string> Format = v => v.ToString("0") + "%";
    static readonly double[] Jitter = MakeJitter();
    static readonly FontFamily Mincho = new("Yu Mincho, MS PMincho, Georgia");
    static readonly Brush InkDark = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x18, 0x14)));
    const int Steps = 120;
    // Bristle pens for the current size and ink, built once instead of ~480 per frame.
    Pen[]? _bristles;
    (double R, Brush Ink) _bristlesFor;
    // Slow and brush-like, but quick enough to come to rest between once-a-second readings.
    protected override double Speed => 5;
    protected override double Range => Max;

    Pen[] Bristles(double r)
    {
        if (_bristles != null && _bristlesFor == (r, P.Ink))
            return _bristles;
        var pens = new Pen[4 * Steps];
        for (int b = 0; b < 4; b++)
        {
            var brush = Alpha(P.Ink, (byte)(b == 0 ? 0xE6 : 0x38));
            for (int i = 0; i < Steps; i++)
            {
                double taper = Math.Pow(Math.Sin(Math.PI * Math.Min(1, i / (double)Steps * 1.1 + 0.02)), 0.6);
                double width = Math.Max(1, r * 0.15 * (0.25 + 0.75 * taper) * (b == 0 ? 1 : 0.35));
                var p = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                p.Freeze();
                pens[b * Steps + i] = p;
            }
        }
        _bristlesFor = (r, P.Ink);
        return _bristles = pens;
    }

    static double[] MakeJitter()
    {
        var rnd = new Random(7);
        return Enumerable.Range(0, 600).Select(_ => rnd.NextDouble()).ToArray();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = Math.Min(w, h) * 0.36;
        if (r < 12) return;
        var c = new Point(w / 2, h / 2);
        double f = Math.Clamp(V("v") / Max, 0, 1), span = 340 * Math.Max(0.04, f);

        dc.DrawEllipse(null, MakePen(Alpha(P.Ink, 0x10), r * 0.16), c, r, r);
        if (Has("v"))
        {
            var pens = Bristles(r);
            for (int b = 0; b < 4; b++)
            {
                for (int i = 0; i < Steps; i++)
                {
                    double t0 = i / (double)Steps, t1 = (i + 1) / (double)Steps;
                    double rr = r + (b - 1.5) * r * 0.045 + (Jitter[(i * 4 + b) % Jitter.Length] - 0.5) * r * 0.05;
                    dc.DrawLine(pens[b * Steps + i], Polar(c, rr, -70 + span * t0), Polar(c, rr, -70 + span * t1));
                }
            }
        }
        var mincho = Mincho;
        Text(dc, Has("v") ? Format(V("v")) : Dash, c.X, c.Y - r * 0.04, r * 0.48, P.Ink, mincho, weight: FontWeights.SemiBold);

        double s = r * 0.34;
        var seal = new Rect(c.X + r * 0.62, c.Y + r * 0.62, s, s);
        dc.DrawRoundedRectangle(Theme.Current.Japanese ? Theme.Accent : P.Crit, null, seal, 3, 3);
        var inkDark = InkDark;
        if (Seal.Length >= 2)
        {
            Text(dc, Seal[0].ToString(), seal.X + s / 2, seal.Y + s * 0.3, s * 0.4, inkDark, mincho, weight: FontWeights.SemiBold);
            Text(dc, Seal[1].ToString(), seal.X + s / 2, seal.Y + s * 0.72, s * 0.4, inkDark, mincho, weight: FontWeights.SemiBold);
        }
        else
        {
            Text(dc, Seal, seal.X + s / 2, seal.Y + s / 2, s * 0.6, inkDark, mincho, weight: FontWeights.SemiBold);
        }
    }
}
