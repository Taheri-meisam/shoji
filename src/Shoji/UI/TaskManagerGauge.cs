using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shoji.Core;
using Shoji.Sensors;
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
using ThemeT = Shoji.UI.Theme;

namespace Shoji.UI;

/// <summary>
/// The Task manager tile's picture, written once for both dashboards: a live stream graph of the apps using the
/// CPU, memory or disk over the last minute or two (each app a band in its own colour, the rest of the machine a
/// faint band on top), the current total in big digits, and a ranked list with a sparkline per app.
/// Redrawn once per sample (about a second); with animations on, the graph glides one step left meanwhile.
/// Each app keeps its colour while it stays in view, so a band and its row are easy to match.
/// </summary>
public sealed class TaskManagerGauge : Gauge
{
    readonly ProcessHistory _history = ProcessHistory.Shared;
    ProcessMetric _metric;
    long _version = -1;

    /// <summary>Seconds of history in the graph (30–120).</summary>
    public int Seconds = 90;

    /// <summary>
    /// The "Rings" style: a round dial instead of the flat graph. The outer ring is the same stream graph wrapped
    /// around a clock face (now at 12 o'clock, older seconds running back anticlockwise), the inner ring is each
    /// app's share right now, and the total sits in the middle.
    /// </summary>
    public bool Ring;

    double? _memUsed, _memTotal;

    /// <summary>The machine's memory in use and installed (MB), for the memory view's headline and caption.</summary>
    public void SetMemory(double usedMb, double totalMb)
    {
        if (_memUsed == usedMb && _memTotal == totalMb)
            return;
        _memUsed = usedMb;
        _memTotal = totalMb;
        if (_metric == ProcessMetric.Memory)
            InvalidateVisual();
    }

    double? MemoryUsedMb => _memUsed;
    double? MemoryTotalMb => _memTotal;

    public ProcessMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
                return;
            _metric = value;
            Refresh(force: true);
        }
    }

    // Eased readings: "n" (the glide, in samples) and "scale" (log2 of the graph's top), both about 1 per step.
    protected override double Range => 1;
    protected override double Speed => 11;

    /// <summary>Picks up a new sample (called every refresh; does nothing until the history has grown).</summary>
    public void Refresh(bool force = false)
    {
        var samples = _history.Samples;
        if (!force && _history.Version == _version)
            return;
        _version = _history.Version;
        if (samples.Count == 0)
        {
            InvalidateVisual();
            return;
        }
        var last = samples[^1];
        Set("n", last.Sample); // the glide: shown "n" trails the newest sample by up to one step
        // Each view eases its own scale, so switching views doesn't sweep from the other one's.
        Set(ScaleKey, Math.Log2(NiceMax(Window(samples).Max(t => ProcessHistory.Total(t, _metric)))));
        InvalidateVisual();
    }

    string ScaleKey => "scale" + (int)_metric;

    IEnumerable<ProcessTable> Window(IReadOnlyList<ProcessTable> samples) =>
        samples.Skip(Math.Max(0, samples.Count - (Math.Clamp(Seconds, 30, 120) + 2)));

    double NiceMax(double v)
    {
        if (_metric == ProcessMetric.Cpu)
            v = Math.Min(v, 100);
        double floor = _metric switch { ProcessMetric.Cpu => 5, ProcessMetric.Memory => 512, _ => 1_048_576 };
        v = Math.Max(v * 1.12, floor);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var k in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (k * mag >= v)
                return _metric == ProcessMetric.Cpu ? Math.Min(100, k * mag) : k * mag;
        return v;
    }

    // ---------------------------------------------------------------- colours

    readonly Dictionary<string, (int Slot, long Seen)> _slots = new(StringComparer.Ordinal);
    Color[]? _palette;

    /// <summary>
    /// Eight colours that sit with the theme: its two accents first, then hues spread around the accent at its
    /// saturation and lightness (kept readable on the tile). A grey accent (ink themes) becomes shades instead.
    /// </summary>
    Color[] Palette()
    {
        if (_palette != null)
            return _palette;
        var accent = ThemeT.AccentColor;
        bool light = ThemeT.Luminance(FaceColor()) > 0.45;
        var (h, s, l) = ToHsl(accent);
        var list = new List<Color> { accent, ThemeT.Accent2Color };
        if (s < 0.15)
        {
            foreach (var k in new[] { 0.78, 0.6, 0.45, 0.33, 0.25, 0.18 })
                list.Add(Mix(accent, light ? Colors.Black : Colors.White, light ? 1 - k : k * 0.6));
        }
        else
        {
            // Ink-and-paper themes get quieter, earthier colours than the neon ones.
            bool quiet = ThemeT.Current.Japanese;
            s = quiet ? Math.Clamp(s, 0.22, 0.32) : Math.Clamp(s, 0.45, 0.8);
            l = light ? Math.Clamp(l, 0.36, 0.5) : quiet ? Math.Clamp(l, 0.55, 0.64) : Math.Clamp(l, 0.58, 0.7);
            foreach (var turn in new[] { 140, 60, 200, 290, 30, 250 })
                list.Add(FromHsl((h + turn) % 360, s, l));
        }
        return _palette = list.ToArray();
    }

    Color FaceColor() =>
#if SHOJI_LINUX
        (P.Face as ISolidColorBrush)?.Color ?? ThemeT.TileColor;
#else
        (P.Face as SolidColorBrush)?.Color ?? ThemeT.BackgroundColor;
#endif

    /// <summary>The colour of an app; it keeps the same one while on screen, and a newcomer takes the longest-unused one.</summary>
    Color ColorFor(string name, HashSet<string> visible, long stamp)
    {
        var pal = Palette();
        if (_slots.TryGetValue(name, out var have))
        {
            _slots[name] = (have.Slot, stamp);
            return pal[have.Slot];
        }
        var taken = _slots.Where(kv => visible.Contains(kv.Key) && kv.Key != name).Select(kv => kv.Value.Slot).ToHashSet();
        int slot = Enumerable.Range(0, pal.Length).Where(i => !taken.Contains(i))
            .OrderBy(i => _slots.Where(kv => kv.Value.Slot == i).Select(kv => kv.Value.Seen).DefaultIfEmpty(long.MinValue).Max())
            .DefaultIfEmpty(_slots.Count % pal.Length).First();
        foreach (var old in _slots.Where(kv => kv.Value.Slot == slot).Select(kv => kv.Key).ToList())
            _slots.Remove(old);
        _slots[name] = (slot, stamp);
        return pal[slot];
    }

    // ---------------------------------------------------------------- drawing

#if SHOJI_LINUX
    protected override void Draw(DrawingContext dc) => Paint(dc, Bounds.Width, Bounds.Height);
#else
    protected override void OnRender(DrawingContext dc) => Paint(dc, ActualWidth, ActualHeight);
#endif

    void Paint(DrawingContext dc, double w, double h)
    {
        if (w < 60 || h < 40)
            return;
        var samples = _history.Samples;
        if (samples.Count == 0)
        {
            Text(dc, L.T("Measuring…"), w / 2, h / 2, 13, P.Dim, P.Font);
            return;
        }

        var window = Window(samples).ToList();
        var now = window[^1];
        var lookups = window.Select(t => t.Apps.ToDictionary(a => a.Name, StringComparer.Ordinal)).ToList();

        // Bands: the apps with the most use over the window, the biggest at the bottom.
        bool wide = w >= 470;
        int bandCount = wide ? 6 : 5;
        var bands = lookups.SelectMany(d => d.Values)
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Sum: g.Sum(a => ProcessHistory.Value(a, _metric))))
            .Where(b => b.Sum > 0)
            .OrderByDescending(b => b.Sum)
            .Take(bandCount)
            .Select(b => b.Name)
            .ToList();

        // Rows: right now's leaders.
        var rows = now.Apps.Where(a => ProcessHistory.Value(a, _metric) > 0 || _metric != ProcessMetric.Disk)
            .OrderByDescending(a => ProcessHistory.Value(a, _metric)).ThenByDescending(a => a.MemoryMb).Take(8).ToList();

        var visible = new HashSet<string>(bands, StringComparer.Ordinal);
        visible.UnionWith(rows.Select(r => r.Name));
        var colors = visible.ToDictionary(n => n, n => ColorFor(n, visible, now.Sample), StringComparer.Ordinal);

        Rect graph, list;
        if (Ring)
        {
            // The dial is square; the list takes what's left beside it, or below it on a tall narrow tile.
            if (w >= 400)
            {
                double d = Math.Min(h, w * 0.46);
                graph = new Rect(0, (h - d) / 2, d, d);
                list = new Rect(d + 20, 0, Math.Max(0, w - d - 20), h);
            }
            else if (h >= w * 1.25)
            {
                double d = Math.Min(w, h * 0.56);
                graph = new Rect((w - d) / 2, 0, d, d);
                list = new Rect(0, d + 10, w, Math.Max(0, h - d - 10));
            }
            else
            {
                double d = Math.Min(w, h);
                graph = new Rect((w - d) / 2, (h - d) / 2, d, d);
                list = default;
            }
        }
        else if (wide)
        {
            double gw = Math.Round(w * 0.56);
            graph = new Rect(0, 0, gw, h);
            list = new Rect(gw + 18, 0, Math.Max(0, w - gw - 18), h);
        }
        else if (h >= 170)
        {
            double gh = Math.Round(h * 0.5);
            graph = new Rect(0, 0, w, gh);
            list = new Rect(0, gh + 8, w, Math.Max(0, h - gh - 8));
        }
        else
        {
            graph = new Rect(0, 0, w, h);
            list = default;
        }

        if (Ring)
            DrawDial(dc, graph, window, lookups, bands, colors, now);
        else
            DrawGraph(dc, graph, window, lookups, bands, colors, now);
        if (list.Width > 80 && list.Height > 24)
            DrawList(dc, list, rows, lookups, colors);
    }

    void DrawGraph(DrawingContext dc, Rect area, List<ProcessTable> window, List<Dictionary<string, ProcessInfo>> lookups,
                   List<string> bands, Dictionary<string, Color> colors, ProcessTable now)
    {
        // Headline: the total in big digits, what it is underneath.
        double big = Math.Clamp(area.Height * 0.17, 18, 34);
        // Memory: the machine's own figure. Apps share memory, so their sizes added up can come to more than is in use.
        double total = _metric == ProcessMetric.Memory && MemoryUsedMb is double used ? used : ProcessHistory.Total(now, _metric);
        var (num, unit) = Split(Format(total, _metric, headline: true));
        var size = Text(dc, num, area.X, area.Y + big * 0.55, big, P.Fg, P.Display, Align.Left, Semi, P.Italic);
        double ux = area.X + size.Width + 3;
        var usize = Text(dc, unit, ux, area.Y + big * 0.55 + big * 0.14, big * 0.48, P.Dim, P.Display, Align.Left);
        double capX = ux + usize.Width + 12, capSize = Math.Clamp(big * 0.36, 10, 12);
        if (capX < area.Right - 60)
        {
            Text(dc, Caption(now), capX, area.Y + big * 0.32, capSize, P.Fg, P.Font, Align.Left);
            Text(dc, L.F("{0} processes", now.ProcessCount), capX,
                area.Y + big * 0.32 + capSize + 3, capSize, P.Dim, P.Font, Align.Left);
        }

        double top = area.Y + big + 10, axis = area.Height >= 110 ? 14 : 0;
        // The level labels get a gutter of their own on the right, clear of the live edge.
        double gutter = area.Width >= 220 ? 40 : 0;
        var plot = new Rect(area.X, top, area.Width - gutter, Math.Max(10, area.Bottom - top - axis));
        if (plot.Height < 24)
            return;

        int span = Math.Clamp(Seconds, 30, 120);
        double step = plot.Width / span, max = Math.Pow(2, V(ScaleKey, Math.Log2(NiceMax(window.Max(t => ProcessHistory.Total(t, _metric))))));
        double glide = Math.Clamp(now.Sample - V("n", now.Sample), 0, 1);
        double Y(double v) => plot.Bottom - Math.Clamp(v / max, 0, 1.04) * plot.Height;
        double X(int i) => plot.Right - (window.Count - 1 - i - glide) * step;

        // Grid: a baseline and two dashed levels labelled at the right.
        var grid = DashPen(P.Track, 1);
        dc.DrawLine(MakePen(P.Track, 1), new Point(plot.X, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));
        foreach (var f in new[] { 0.5, 1.0 })
        {
            double y = Math.Round(plot.Bottom - plot.Height * f) + 0.5;
            dc.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
            if (gutter > 0)
                Text(dc, Format(max * f, _metric, headline: false), area.Right, y, 9.5, P.Dim, P.Mono, Align.Right);
            else
                Text(dc, Format(max * f, _metric, headline: false), plot.Right - 2, y + 7, 9.5, P.Dim, P.Mono, Align.Right);
        }
        if (axis > 0)
        {
            double ty = plot.Bottom + axis / 2 + 2;
            for (int s = span; s > 0; s -= span >= 90 ? 30 : 15)
                Text(dc, $"-{s}s", plot.Right - s * step, ty, 9.5, P.Dim, P.Mono, s == span ? Align.Left : Align.Center);
            Text(dc, L.T("now"), plot.Right, ty, 9.5, P.Accent, P.Mono, Align.Right);
        }

        // Just started: say why most of the graph is empty instead of leaving a blank box.
        double emptyRight = X(0) - 8;
        if (emptyRight - plot.X > 150 && plot.Height > 40)
            Text(dc, L.F("History fills in over {0} s", span), plot.X + (emptyRight - plot.X) / 2, plot.Y + plot.Height / 2,
                 10.5, P.Dim, P.Font);

        if (window.Count < 2)
            return;

        // Stack the bands; "the rest" (everything else running) goes on top up to the machine's total.
        int n = window.Count;
        var lower = new double[n];
        var layers = new List<(double[] Lo, double[] Hi, Color? Color)>();
        foreach (var name in bands)
        {
            var hi = new double[n];
            var own = Smooth(lookups.Select(d => d.TryGetValue(name, out var a) ? ProcessHistory.Value(a, _metric) : 0).ToArray());
            for (int i = 0; i < n; i++)
                hi[i] = lower[i] + own[i];
            layers.Add(((double[])lower.Clone(), hi, colors[name]));
            lower = hi;
        }
        var totals = Smooth(window.Select(t => ProcessHistory.Total(t, _metric)).ToArray());
        var rest = new double[n];
        for (int i = 0; i < n; i++)
            rest[i] = Math.Max(lower[i], totals[i]);

        PushClip(dc, new Rect(plot.X, area.Y, plot.Width, plot.Bottom - area.Y + 1));
        var restColor = DimColor();
        Band(dc, lower, rest, X, Y, Solid(restColor, 0x22), null);
        dc.DrawGeometry(null, MakePen(Solid(restColor, 0x70), 1), Curve(rest, X, Y));
        foreach (var (lo, hi, c) in layers)
        {
            var col = c!.Value;
            var fill = Gradient(plot, col);
            Band(dc, lo, hi, X, Y, fill, null);
            Stroke(dc, Curve(hi, X, Y), Solid(col, 0xFF), 1.4);
        }
        Pop(dc);

        // The live edge: a hairline at "now" and a dot on the total.
        double ex = X(n - 1);
        if (ex <= plot.Right + 0.5)
        {
            dc.DrawLine(MakePen(Solid(ThemeT.AccentColor, 0x50), 1), new Point(ex, plot.Y), new Point(ex, plot.Bottom));
            Dot(dc, new Point(ex, Y(rest[n - 1])), 2.6, P.Accent);
        }
    }

    void DrawDial(DrawingContext dc, Rect box, List<ProcessTable> window, List<Dictionary<string, ProcessInfo>> lookups,
                  List<string> bands, Dictionary<string, Color> colors, ProcessTable now)
    {
        double R = Math.Min(box.Width, box.Height) / 2 - 3;
        if (R < 30)
            return;
        var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        double outer = R, inner = R * 0.64;            // the history ring
        double donutR = R * 0.5, donutW = Math.Max(4, R * 0.075);
        int span = Math.Clamp(Seconds, 30, 120), n = window.Count;
        const double Sweep = 346;                      // degrees of history; the gap at the top keeps now apart from the oldest second
        double glide = Math.Clamp(now.Sample - V("n", now.Sample), 0, 1);
        double max = Math.Pow(2, V(ScaleKey, Math.Log2(NiceMax(window.Max(t => ProcessHistory.Total(t, _metric))))));
        double Angle(double age) => -90 - age / span * Sweep;
        double Radius(double v) => inner + Math.Clamp(v / max, 0, 1.04) * (outer - inner);

        // Scale: faint circles at the base, half and top of the ring; ticks every 15 s along the inside.
        dc.DrawEllipse(null, MakePen(P.Track, 1), c, inner, inner);
        dc.DrawEllipse(null, DashPen(P.Track, 1), c, (inner + outer) / 2, (inner + outer) / 2);
        dc.DrawEllipse(null, DashPen(P.Track, 1), c, outer, outer);
        for (int sec = 0; sec <= span; sec += 15)
        {
            double a = Angle(sec);
            dc.DrawLine(MakePen(sec == 0 ? P.Accent : P.Dim, sec == 0 ? 1.6 : 1), Polar(c, inner - (sec == 0 ? 7 : 4), a), Polar(c, inner - 1, a));
        }
        if (R >= 70)
        {
            // Scale labels in the gap at the top: the ring's top value on the outer edge, half way in the middle.
            Text(dc, Format(max, _metric, headline: false), c.X + 4, c.Y - outer + 1, 9, P.Dim, P.Mono, Align.Left);
        }

        if (n >= 2)
        {
            // Stack the apps as rings of history; the rest of the machine on top, faint.
            var ages = Enumerable.Range(0, n).Select(i => n - 1 - i - glide).ToArray();
            int first = Array.FindIndex(ages, a => a <= span);
            first = Math.Max(0, first < 0 ? 0 : first);
            var lower = new double[n];
            var layers = new List<(double[] Lo, double[] Hi, Color Color)>();
            foreach (var name in bands)
            {
                var own = Smooth(lookups.Select(d => d.TryGetValue(name, out var a) ? ProcessHistory.Value(a, _metric) : 0).ToArray());
                var hi = new double[n];
                for (int i = 0; i < n; i++)
                    hi[i] = lower[i] + own[i];
                layers.Add(((double[])lower.Clone(), hi, colors[name]));
                lower = hi;
            }
            var totals = Smooth(window.Select(t => ProcessHistory.Total(t, _metric)).ToArray());
            var rest = new double[n];
            for (int i = 0; i < n; i++)
                rest[i] = Math.Max(lower[i], totals[i]);

            Geometry Ring2(double[] lo, double[] hi) => Shape(ctx =>
            {
                ctx.BeginFigure(Polar(c, Radius(hi[first]), Angle(ages[first])), true, true);
                for (int i = first + 1; i < n; i++)
                    ctx.LineTo(Polar(c, Radius(hi[i]), Angle(ages[i])), true, false);
                for (int i = n - 1; i >= first; i--)
                    ctx.LineTo(Polar(c, Radius(lo[i]), Angle(ages[i])), true, false);
            });
            Geometry Edge(double[] hi) => Shape(ctx =>
            {
                ctx.BeginFigure(Polar(c, Radius(hi[first]), Angle(ages[first])), false, false);
                for (int i = first + 1; i < n; i++)
                    ctx.LineTo(Polar(c, Radius(hi[i]), Angle(ages[i])), true, false);
            });

            var dim = DimColor();
            dc.DrawGeometry(Solid(dim, 0x22), null, Ring2(lower, rest));
            dc.DrawGeometry(null, MakePen(Solid(dim, 0x70), 1), Edge(rest));
            foreach (var (lo, hi, col) in layers)
            {
                dc.DrawGeometry(Solid(col, 0x9C), null, Ring2(lo, hi));
                Stroke(dc, Edge(hi), Solid(col, 0xFF), 1.3);
            }
            // The live point: where the newest total sits at 12 o'clock.
            Dot(dc, Polar(c, Radius(rest[n - 1]), Angle(ages[n - 1])), 2.8, P.Accent);
        }

        // Inner ring: right now, each app's share of the whole (CPU of 100 %, memory of what's installed).
        double total = _metric == ProcessMetric.Memory && MemoryUsedMb is double used ? used : ProcessHistory.Total(now, _metric);
        double capacity = _metric switch
        {
            ProcessMetric.Cpu => 100,
            ProcessMetric.Memory => MemoryTotalMb ?? Math.Max(total, 1),
            _ => Math.Max(total, 1e-9),
        };
        dc.DrawGeometry(null, MakePen(P.Track, donutW), Arc(c, donutR, 0, 360));
        double at = -90, gap = 1.6, used2 = 0;
        var lookupNow = now.Apps.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (var name in bands)
        {
            double v = lookupNow.TryGetValue(name, out var a) ? ProcessHistory.Value(a, _metric) : 0;
            double deg = Math.Clamp(v / capacity, 0, 1) * 360;
            if (at + deg > 270) deg = 270 - at;
            if (deg > gap + 0.4)
                Stroke(dc, Arc(c, donutR, at, at + deg - gap), Solid(colors[name], 0xFF), donutW);
            at += deg;
            used2 += v;
        }
        double restDeg = Math.Clamp((total - used2) / capacity, 0, 1) * 360;
        if (at + restDeg > 270) restDeg = 270 - at;
        if (restDeg > gap + 0.4)
            dc.DrawGeometry(null, MakePen(Solid(DimColor(), 0x90), donutW), Arc(c, donutR, at, at + restDeg - gap));

        // Centre: the total, and what it is.
        double big = Math.Clamp(donutR * 0.5, 12, 40);
        var (num, unit) = Split(Format(total, _metric, headline: true));
        string label = _metric switch { ProcessMetric.Memory => L.T("RAM"), ProcessMetric.Disk => DiskLabel, _ => L.T("CPU") };
        var ns = Text(dc, num, c.X, c.Y - big * 0.12, big, P.Fg, P.Display, Align.Center, Semi, P.Italic);
        if (donutR >= 34)
        {
            Text(dc, unit.Length > 0 ? $"{unit} · {label}" : label, c.X, c.Y + big * 0.55, Math.Clamp(big * 0.3, 9, 12), P.Dim, P.Mono, Align.Center);
            if (donutR >= 60)
                Text(dc, L.F("{0} processes", now.ProcessCount), c.X, c.Y + big * 0.55 + 14, 9.5, P.Dim, P.Font, Align.Center);
        }
    }

#if SHOJI_LINUX
    static string DiskLabel => L.T("Disk");
#else
    static string DiskLabel => L.T("I/O");
#endif

    void DrawList(DrawingContext dc, Rect area, List<ProcessInfo> rows, List<Dictionary<string, ProcessInfo>> lookups,
                  Dictionary<string, Color> colors)
    {
        if (rows.Count == 0)
        {
            Text(dc, _metric == ProcessMetric.Disk ? L.T("No disk activity") : L.T("Measuring…"), area.X + area.Width / 2, area.Y + area.Height / 2, 12, P.Dim, P.Font);
            return;
        }
        double rh = Math.Clamp(area.Height / Math.Min(rows.Count, 6), 22, 34);
        int count = Math.Min(rows.Count, (int)Math.Floor(area.Height / rh));
        rh = count > 0 ? Math.Min(34, area.Height / count) : rh;
        double fs = Math.Clamp(rh * 0.45, 11, 14);
        bool spark = area.Width >= 230, second = area.Width >= 290;
        var secondary = _metric == ProcessMetric.Cpu ? ProcessMetric.Memory : ProcessMetric.Cpu;
        // Columns as wide as their widest value ("25%" vs "53 MB/s"), so nothing runs into its neighbour.
        double valueW = rows.Take(count).Max(r => Measure(Format(ProcessHistory.Value(r, _metric), _metric, false), fs, P.Mono)) + 8;
        double secondW = second ? rows.Take(count).Max(r => Measure(Format(ProcessHistory.Value(r, secondary), secondary, false), fs * 0.82, P.Mono)) + 10 : 0;
        double sparkW = spark ? Math.Min(64, area.Width * 0.2) : 0;
        double rowMax = Math.Max(1e-9, rows.Take(count).Max(r => ProcessHistory.Value(r, _metric)));
        int sparkLen = Math.Min(lookups.Count, 40);

        for (int i = 0; i < count; i++)
        {
            var r = rows[i];
            var col = colors.TryGetValue(r.Name, out var c) ? c : DimColor();
            double y = area.Y + i * rh, cy = y + rh * 0.44, v = ProcessHistory.Value(r, _metric);
            var brush = Solid(col, 0xFF);

            // Colour key, name, instances.
            RoundRect(dc, brush, new Rect(area.X, cy - rh * 0.26, 3.5, rh * 0.52), 1.75);
            double nameX = area.X + 11, right = area.Right;
            double nameRoom = right - valueW - secondW - sparkW - (spark ? 10 : 4) - nameX;
            string name = Fit(r.Name, nameRoom, fs, P.Font);
            var ns = Text(dc, name, nameX, cy, fs, P.Fg, P.Font, Align.Left);
            if (r.Instances > 1 && ns.Width + 26 < nameRoom)
                Text(dc, $"×{r.Instances}", nameX + ns.Width + 5, cy + 1, fs * 0.78, P.Dim, P.Mono, Align.Left);

            // Values: the chosen one bright, the other one dim.
            bool hot = _metric == ProcessMetric.Cpu && v >= 25 || _metric == ProcessMetric.Disk && v >= 50 * 1048576;
            Text(dc, Format(v, _metric, headline: false), right, cy, fs, hot ? P.Warn : P.Fg, P.Mono, Align.Right);
            if (second)
                Text(dc, Format(ProcessHistory.Value(r, secondary), secondary, headline: false), right - valueW, cy, fs * 0.82, P.Dim, P.Mono, Align.Right);

            // Sparkline of this app over the last ~40 s, on its own scale.
            if (spark && sparkLen >= 2)
            {
                var data = new double[sparkLen];
                for (int k = 0; k < sparkLen; k++)
                {
                    var d = lookups[lookups.Count - sparkLen + k];
                    data[k] = d.TryGetValue(r.Name, out var a) ? ProcessHistory.Value(a, _metric) : 0;
                }
                double sMax = Math.Max(data.Max(), rowMax * 0.15);
                var box = new Rect(right - valueW - secondW - sparkW - 4, y + rh * 0.16, sparkW, rh * 0.56);
                double SX(int k) => box.X + k * box.Width / (sparkLen - 1);
                double SY(double val) => box.Bottom - Math.Clamp(val / Math.Max(sMax, 1e-9), 0, 1) * box.Height;
                var line = Curve(data, SX, SY);
                var area2 = Shape(ctx =>
                {
                    ctx.BeginFigure(new Point(box.X, box.Bottom), true, true);
                    for (int k = 0; k < sparkLen; k++)
                        ctx.LineTo(new Point(SX(k), SY(data[k])), true, false);
                    ctx.LineTo(new Point(box.Right, box.Bottom), true, false);
                });
                dc.DrawGeometry(Solid(col, 0x30), null, area2);
                dc.DrawGeometry(null, MakePen(brush, 1.2), line);
            }

            // Share bar along the bottom of the row.
            double by = y + rh - 3, bw = right - nameX;
            RoundRect(dc, P.Track, new Rect(nameX, by, bw, 2), 1);
            RoundRect(dc, Solid(col, 0xB0), new Rect(nameX, by, Math.Max(2, bw * Math.Clamp(v / rowMax, 0, 1)), 2), 1);
        }
    }

    // ---------------------------------------------------------------- text

    string Caption(ProcessTable now) => _metric switch
    {
        ProcessMetric.Memory => MemoryUsedMb is double && MemoryTotalMb is double t && t > 0
            ? L.F("RAM in use, of {0}", Format(t, ProcessMetric.Memory, false))
            : L.T("RAM in apps"),
#if SHOJI_LINUX
        ProcessMetric.Disk => L.T("Disk read + write"),
#else
        ProcessMetric.Disk => L.T("Read + write (disk, network, pipes)"),
#endif
        _ => L.T("CPU, all cores"),
    };

    public static string Format(double v, ProcessMetric m, bool headline)
    {
        var ci = CultureInfo.CurrentCulture;
        switch (m)
        {
            case ProcessMetric.Memory:
                return v >= 1024 ? (v / 1024).ToString(v >= 10240 ? "0" : "0.0", ci) + " GB" : v.ToString("0", ci) + " MB";
            case ProcessMetric.Disk:
                if (v >= 1048576)
                    return (v / 1048576).ToString(v >= 10485760 ? "0" : "0.0", ci) + " MB/s";
                if (v >= 1024 || headline)
                    return (v / 1024).ToString("0", ci) + " KB/s";
                return v > 0 ? "<1 KB/s" : "0";
            default:
                return v.ToString(v < 10 && !headline || v < 1 ? "0.0" : "0", ci) + "%";
        }
    }

    /// <summary>"47%" → ("47", "%"), "14.2 GB" → ("14.2", "GB").</summary>
    static (string, string) Split(string s)
    {
        if (s.EndsWith('%'))
            return (s[..^1], "%");
        int sp = s.IndexOf(' ');
        return sp > 0 ? (s[..sp], s[(sp + 1)..]) : (s, "");
    }

    /// <summary>Cuts a name with "…" to fit the width.</summary>
    string Fit(string s, double width, double size, FontFamily font)
    {
        if (width <= 12)
            return "";
        if (Measure(s, size, font) <= width)
            return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(s[..mid] + "…", size, font) <= width) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? "" : s[..lo].TrimEnd() + "…";
    }

    double Measure(string s, double size, FontFamily font)
    {
#if SHOJI_LINUX
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font), size, P.Fg).Width;
#else
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, P.Fg, VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
#endif
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// A smooth curve through the points with level tangents at each one. Every curve of the stack bends with the same
    /// weights between two samples, so a band's top never dips below its bottom (Catmull-Rom curves can overshoot).
    /// </summary>
    static Geometry Curve(double[] v, Func<int, double> x, Func<double, double> y) => Shape(ctx =>
    {
        ctx.BeginFigure(new Point(x(0), y(v[0])), false, false);
        Trace(ctx, v, x, y, forward: true);
    });

    /// <summary>Takes the edge off one-second jitter (1-2-1 weights); the newest point stays as measured.</summary>
    static double[] Smooth(double[] v)
    {
        if (v.Length < 3)
            return v;
        var r = new double[v.Length];
        r[0] = (v[0] * 3 + v[1]) / 4;
        for (int i = 1; i < v.Length - 1; i++)
            r[i] = (v[i - 1] + v[i] * 2 + v[i + 1]) / 4;
        r[^1] = v[^1];
        return r;
    }

    static void Trace(Ctx ctx, double[] v, Func<int, double> x, Func<double, double> y, bool forward)
    {
        int n = v.Length;
        for (int k = 1; k < n; k++)
        {
            int a = forward ? k - 1 : n - k, b = forward ? k : n - k - 1;
            double x0 = x(a), x1 = x(b), mx = (x0 + x1) / 2;
            ctx.BezierTo(new Point(mx, y(v[a])), new Point(mx, y(v[b])), new Point(x1, y(v[b])), true, false);
        }
    }

    static void Band(DrawingContext dc, double[] lo, double[] hi, Func<int, double> x, Func<double, double> y, BrushT fill, Pen? pen)
    {
        var g = Shape(ctx =>
        {
            ctx.BeginFigure(new Point(x(0), y(hi[0])), true, true);
            Trace(ctx, hi, x, y, forward: true);
            ctx.LineTo(new Point(x(hi.Length - 1), y(lo[^1])), true, false);
            Trace(ctx, lo, x, y, forward: false);
        });
        dc.DrawGeometry(fill, pen, g);
    }

    // ---------------------------------------------------------------- colour helpers

    Color DimColor() =>
#if SHOJI_LINUX
        (P.Dim as ISolidColorBrush)?.Color ?? ThemeT.DimColor;
#else
        (P.Dim as SolidColorBrush)?.Color ?? ThemeT.TextColor;
#endif

    readonly Dictionary<(Color, byte), BrushT> _solids = new();

    BrushT Solid(Color c, byte alpha)
    {
        if (_solids.TryGetValue((c, alpha), out var b))
            return b;
        if (_solids.Count > 256)
            _solids.Clear();
#if SHOJI_LINUX
        b = new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
#else
        var s = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        s.Freeze();
        b = s;
#endif
        return _solids[(c, alpha)] = b;
    }

    readonly Dictionary<(Color, double, double), BrushT> _gradients = new();

    /// <summary>A band's fill: strong at the top of the graph, fading toward the baseline (the same light for every band).</summary>
    BrushT Gradient(Rect plot, Color c)
    {
        var key = (c, Math.Round(plot.Y), Math.Round(plot.Bottom));
        if (_gradients.TryGetValue(key, out var b))
            return b;
        if (_gradients.Count > 64)
            _gradients.Clear();
        var top = Color.FromArgb(0xD8, c.R, c.G, c.B);
        var bottom = Color.FromArgb(0x48, c.R, c.G, c.B);
#if SHOJI_LINUX
        b = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, plot.Y, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, plot.Bottom, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
#else
        var g = new LinearGradientBrush(top, bottom, new Point(0, plot.Y), new Point(0, plot.Bottom)) { MappingMode = BrushMappingMode.Absolute };
        g.Freeze();
        b = g;
#endif
        return _gradients[key] = b;
    }

    static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6)
            return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double C(double t)
        {
            t = (t % 1 + 1) % 1;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        double hh = h / 360;
        return Color.FromRgb((byte)Math.Round(C(hh + 1 / 3.0) * 255), (byte)Math.Round(C(hh) * 255), (byte)Math.Round(C(hh - 1 / 3.0) * 255));
    }

    // ---------------------------------------------------------------- WPF / Avalonia differences

#if SHOJI_LINUX
    static readonly FontWeight Semi = FontWeight.SemiBold;
#else
    static readonly FontWeight Semi = FontWeights.SemiBold;
#endif

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

    Pen DashPen(BrushT b, double width)
    {
#if SHOJI_LINUX
        return new Pen(b, width, new DashStyle(new double[] { 3, 4 }, 0));
#else
        var p = new Pen(b, width) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        p.Freeze();
        return p;
#endif
    }

    static void RoundRect(DrawingContext dc, BrushT brush, Rect r, double radius)
    {
#if SHOJI_LINUX
        dc.DrawRectangle(brush, null, r, radius, radius);
#else
        dc.DrawRoundedRectangle(brush, null, r, radius, radius);
#endif
    }

    static void PushClip(DrawingContext dc, Rect r)
    {
#if SHOJI_LINUX
        dc.PushClipW(r);
#else
        dc.PushClip(new RectangleGeometry(r));
#endif
    }

    static void Pop(DrawingContext dc)
    {
#if SHOJI_LINUX
        dc.PopW();
#else
        dc.Pop();
#endif
    }
}
