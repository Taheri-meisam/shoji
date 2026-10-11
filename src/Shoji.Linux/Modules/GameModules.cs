using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Frame rate of the app in focus.</summary>
public sealed class FpsModule : Module
{
    readonly TextBlock _fps, _fpsCaption, _low, _frameTime, _app;
    readonly Sparkline _history = new("fps", fixedMax: null);

    public FpsModule(ModuleConfig config) : base(config, "FPS")
    {
        _app = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var stats = W.Columns(W.Stat("fps", out _fps, out _fpsCaption, UI.Theme.Big + 10), W.Stat("1% low", out _low));
        var details = W.Row("Frame time", out _frameTime);
        details.Margin = new Thickness(0, 6, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, details.ShowFrom(Density.Normal)), _app);
    }

    public override void Update(Snapshot s)
    {
        var f = s.Fps;
        _app.Text = string.IsNullOrEmpty(f?.App) ? s.Foreground.ProcessName : f.App; // the game MangoHud is logging
        if (f?.Error != null)
        {
            _fps.Text = "–";
            _fpsCaption.Text = f.Error == "needs admin" ? "needs admin (tray → Restart as admin)" : L.F("FPS counter {0}", L.T(f.Error));
            _fpsCaption.Foreground = UI.Theme.Warn;
            return;
        }

        _fpsCaption.Text = "fps";
        _fpsCaption.Foreground = UI.Theme.Dim;
        _fps.Text = f?.Fps is double fps ? fps.ToString("0") : "–";
        _fps.Foreground = f?.Fps is < 30 ? UI.Theme.Warn : UI.Theme.Text;
        _low.Text = W.Format(f?.OnePercentLow, "0", "");
        _frameTime.Text = W.Format(f?.FrameTimeMs, "0.0", " ms");
        _history.Refresh();
    }
}

/// <summary>Summary of the current or last game session.</summary>
public sealed class RecapModule : Module
{
    readonly TextBlock _game, _when, _note;
    readonly TextBlock _fps, _low, _cpuTemp, _gpuTemp, _cpuLoad, _gpuLoad, _ping;

    public RecapModule(ModuleConfig config) : base(config, "Last session")
    {
        _game = W.Text("", UI.Theme.Medium + 3, weight: FontWeight.SemiBold);
        _game.FontFamily = UI.Theme.Display;
        _when = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        _note = W.Text(L.T("Play something full-screen for 2+ minutes and its summary shows up here."), UI.Theme.Small, UI.Theme.Dim);
        _note.TextWrapping = TextWrapping.Wrap;

        var rows = W.Stack(
            W.SpacedColumns(W.Row("Avg FPS", out _fps), W.Row("Worst 1%", out _low)),
            W.SpacedColumns(W.Row("CPU max", out _cpuTemp), W.Row("GPU max", out _gpuTemp)),
            W.SpacedColumns(W.Row("CPU avg", out _cpuLoad), W.Row("GPU avg", out _gpuLoad)),
            W.Row("Worst ping", out _ping));
        rows.Margin = new Thickness(0, 8, 0, 0);
        View = MakeTile(Body(_note, _game, _when, rows.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var r = s.Recap;
        _note.IsVisible = r == null;
        if (r == null)
            return;

        _game.Text = r.App;
        _when.Text = r.Live
            ? L.F("playing now · {0}", W.Duration(r.Duration))
            : $"{(r.Started.Date == DateTime.Today ? L.T("today") : r.Started.Date == DateTime.Today.AddDays(-1) ? L.T("yesterday") : r.Started.ToString(L.T("ddd d MMM"), L.Culture))} " +
              $"{r.Started:HH:mm} · {W.Duration(r.Duration)}";
        _when.Foreground = r.Live ? UI.Theme.Accent : UI.Theme.Dim;
        _fps.Text = W.Format(r.AverageFps, "0", "");
        _low.Text = W.Format(r.LowFps, "0", "");
        _cpuTemp.Text = W.Temp(r.MaxCpuTemp, unit: true);
        _cpuTemp.Foreground = UI.Theme.ForTemperature(r.MaxCpuTemp);
        _gpuTemp.Text = W.Temp(r.MaxGpuTemp, unit: true);
        _gpuTemp.Foreground = UI.Theme.ForTemperature(r.MaxGpuTemp);
        _cpuLoad.Text = W.Format(r.AverageCpuLoad, "0", "%");
        _gpuLoad.Text = W.Format(r.AverageGpuLoad, "0", "%");
        _ping.Text = W.Format(r.MaxPing, "0", " ms");
    }
}

/// <summary>Per-minute graphs over the last hours, from the shared history (saved across restarts).</summary>
public sealed class HistoryModule : Module
{
    readonly List<string> _series;
    readonly double _hours;
    readonly HistoryChart _chart;
    // Wraps, so three or more series don't run off the tile.
    readonly WrapPanel _legend = new() { Orientation = Orientation.Horizontal };
    DateTime _drawnAt;

    public HistoryModule(ModuleConfig config) : base(config, "History")
    {
        _series = config.OptList("Series");
        if (_series.Count == 0)
            _series = new List<string> { "cpu.temp", "gpu.temp" };
        _hours = Math.Clamp(config.OptInt("Hours", 24), 1, 24);
        _chart = new HistoryChart(_series, _hours);
        _legend.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(_legend, Dock.Top);
        var body = new DockPanel();
        body.Children.Add(_legend);
        body.Children.Add(_chart);
        View = MakeTile(body, W.Text(L.F("last {0:0}h", _hours), UI.Theme.Small, UI.Theme.Dim));
    }

    public override void Update(Snapshot s)
    {
        // Data is per minute; redraw twice a minute is plenty.
        if (DateTime.UtcNow - _drawnAt < TimeSpan.FromSeconds(30))
            return;
        _drawnAt = DateTime.UtcNow;

        _legend.Children.Clear();
        for (int i = 0; i < _series.Count; i++)
        {
            var points = HistoryStore.Shared.Minutes(_series[i], _hours);
            string name = HistoryStore.Names.TryGetValue(_series[i], out var n) ? L.T(n) : _series[i];
            string range = points.Length == 0 ? L.T("no data yet")
                : $"{FormatValue(_series[i], points.Min(p => p.Value))}–{FormatValue(_series[i], points.Max(p => p.Value))}";
            var swatch = new Border { Width = 10, Height = 3, Background = HistoryChart.ColorFor(i), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            item.Children.Add(swatch);
            item.Children.Add(W.Text($"{name}  {range}", UI.Theme.Small - 1, UI.Theme.Dim));
            _legend.Children.Add(item); // swatch and label wrap together
        }
        _chart.InvalidateVisual();
    }

    static string FormatValue(string series, double v) => series switch
    {
        "cpu.temp" or "gpu.temp" => W.Temp(v),
        "net.down" or "net.up" => W.Rate(v),
        _ => v.ToString("0"),
    };
}

/// <summary>Draws several per-minute series, each scaled to its own range, with hour marks.</summary>
public sealed class HistoryChart : DrawnControl
{
    readonly IReadOnlyList<string> _series;
    readonly double _hours;

    public HistoryChart(IReadOnlyList<string> series, double hours)
    {
        _series = series;
        _hours = hours;
        MinHeight = 40;
    }

    public static IBrush ColorFor(int i) => (i % 5) switch
    {
        0 => UI.Theme.Accent,
        1 => UI.Theme.Accent2,
        2 => UI.Theme.Warn,
        3 => UI.Theme.Good,
        _ => UI.Theme.Crit,
    };

    protected override void Draw(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height - 14;
        if (w <= 0 || h <= 0)
            return;

        // Hour marks along the bottom.
        var face = new Typeface(UI.Theme.Font, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
        var gridPen = new Pen(UI.Theme.Track, 1);
        int step = _hours > 12 ? 6 : _hours > 4 ? 2 : 1;
        for (int hr = 0; hr <= _hours; hr += step)
        {
            double x = w - w * hr / _hours;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            var label = new FormattedText(hr == 0 ? "now" : $"-{hr}h", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 10, UI.Theme.Dim);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, w - label.Width)), h + 2));
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        double span = _hours * 60;
        for (int i = 0; i < _series.Count; i++)
        {
            var points = HistoryStore.Shared.Minutes(_series[i], _hours);
            if (points.Length < 2)
                continue;
            double min = points.Min(p => p.Value), max = points.Max(p => p.Value);
            double pad = Math.Max((max - min) * 0.1, 1);
            min -= pad;
            max += pad;

            var geo = new StreamGeometry();
            using (var ctx = geo.OpenW())
            {
                bool started = false;
                long last = 0;
                foreach (var p in points)
                {
                    var pt = new Point(w - (now - p.Minute) / span * w, h - (p.Value - min) / (max - min) * h);
                    // Gaps (app wasn't running) break the line instead of drawing a straight bridge.
                    if (!started || p.Minute - last > 3)
                        ctx.BeginFigure(pt, false, false);
                    else
                        ctx.LineTo(pt, true, true);
                    started = true;
                    last = p.Minute;
                }
            }
            dc.DrawGeometry(null, new Pen(ColorFor(i), 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }
    }
}
