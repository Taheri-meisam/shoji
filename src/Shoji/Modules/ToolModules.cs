using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;
using DockSide = System.Windows.Controls.Dock;
using Paint = System.Windows.Media.Brush;
using Piece = System.Windows.FrameworkElement;

namespace Shoji.Modules;

/// <summary>The few things the tiles below need that differ between the Windows and Linux editions.</summary>
static class ToolKit
{
    // Segoe Fluent Icons / MDL2: Completed, Sync, TouchPointer, ErrorBadge, CommandPrompt.
    public const string Ok = "\uE930", Running = "\uE895", Waiting = "\uE7C9", Failed = "\uEA39", Terminal = "\uE756";
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontWeight Semi = FontWeights.SemiBold;
    public static bool Square => Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
    public static Paint Tint(Paint brush, byte alpha) => Theme.Freeze(new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)brush).Color, alpha)));
    public static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
    public static TaskWatch NewWatch() => new TaskWatch();
    public const string EmptyTasks = "Nothing running. Coding agents and reported tasks show up here.";

    public static TextBlock Glyph(string glyph, double size, Paint color)
    {
        var t = W.Text(glyph, size, color);
        t.FontFamily = IconFont;
        t.TextTrimming = TextTrimming.None;
        t.VerticalAlignment = VerticalAlignment.Top;
        return t;
    }

    /// <summary>A list row: tinted box with a coloured left edge, an icon, and a text column.</summary>
    public static Border Row(Piece? icon, Piece text, Paint edge, bool highlight)
    {
        var line = new DockPanel();
        if (icon != null)
        {
            icon.Margin = new Thickness(0, 1, 9, 0);
            DockPanel.SetDock(icon, DockSide.Left);
            line.Children.Add(icon);
        }
        line.Children.Add(text);
        return new Border
        {
            Child = line, Padding = new Thickness(7, 6, 9, 7), Margin = new Thickness(0, 0, 0, 5),
            CornerRadius = new CornerRadius(Square ? 0 : 8), Background = highlight ? Tint(edge, 0x30) : Theme.Track,
            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = edge,
        };
    }

    /// <summary>"Title … right" on one line: the right part keeps its room, the title is cut with an ellipsis.</summary>
    public static DockPanel Head(TextBlock title, TextBlock right)
    {
        right.Margin = new Thickness(8, 0, 0, 0);
        var top = new DockPanel();
        DockPanel.SetDock(right, DockSide.Right);
        top.Children.Add(right);
        top.Children.Add(title);
        return top;
    }
}

/// <summary>
/// Flight recorder: what went wrong a moment ago and what else was going on right then (Sensors/FlightRecorder.cs).
/// </summary>
public sealed class IncidentsModule : Module
{
    readonly StackPanel _rows = new();
    readonly TextBlock _note;
    IReadOnlyList<Incident>? _shown;
    long _minute = -1;

    public IncidentsModule(ModuleConfig config) : base(config, "Flight recorder")
    {
        _note = W.Text("", Theme.Small, Theme.Dim);
        View = MakeTile(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.FadeOverflow(), _note);
    }

    public override void Update(Snapshot s)
    {
        // Rebuild when something new was noticed, and once a minute for "2 min ago".
        long minute = s.Time.Ticks / TimeSpan.TicksPerMinute;
        if (ReferenceEquals(s.Incidents, _shown) && minute == _minute)
            return;
        _shown = s.Incidents;
        _minute = minute;

        int lastHour = s.Incidents.Count(i => s.Time - i.Time < TimeSpan.FromHours(1));
        _note.Text = s.Incidents.Count == 0 ? "" : lastHour == 0 ? L.T("quiet for an hour") : L.F("{0} in the last hour", lastHour);
        _note.Foreground = lastHour > 0 ? Theme.Warn : Theme.Dim;

        _rows.Children.Clear();
        if (s.Incidents.Count == 0)
        {
            _rows.Children.Add(W.Empty(ToolKit.Ok, L.T("All smooth. Stutters, heat, full memory and ping jumps show up here with what caused them.")));
            return;
        }
        foreach (var i in s.Incidents)
        {
            var color = i.Level >= 2 ? Theme.Crit : Theme.Warn;
            var title = W.Text(i.Title, Theme.Small + 1, color, ToolKit.Semi);
            bool fresh = s.Time - i.Time < TimeSpan.FromSeconds(20);
            var when = W.Text(fresh ? L.T("just now") : i.Time.Date == s.Time.Date ? i.Time.ToString("HH:mm") : i.Time.ToString("ddd HH:mm", L.Culture), Theme.Small - 1, Theme.Dim);
            var cause = W.Text(i.Cause, Theme.Small, Theme.Text);
            cause.TextWrapping = TextWrapping.Wrap;
            cause.TextTrimming = TextTrimming.None;
            _rows.Children.Add(ToolKit.Row(null, W.Stack(ToolKit.Head(title, when), cause), color, fresh));
        }
    }
}

/// <summary>
/// Tasks &amp; agents: commands started with "shoji run" and coding agents' sessions, with the ones waiting for
/// you on top (Sensors/TaskWatch.cs). A finished one says so in a toast wherever you are on the dashboard.
/// </summary>
public sealed class TasksModule : Module
{
    readonly TaskWatch _watch;
    readonly IDashboardHost _host;
    readonly StackPanel _rows = new();
    readonly TextBlock _note;
    readonly List<(TaskItem Item, TextBlock Time)> _times = new();
    string _signature = "?";

    public TasksModule(ModuleConfig config, IDashboardHost host) : base(config, "Tasks & agents")
    {
        _host = host;
        _watch = ServiceStore.Get(TaskWatch.Key, ToolKit.NewWatch);
        _watch.NeedsYou += OnNeedsYou;
        _note = W.Text("", Theme.Small, Theme.Dim);
        View = MakeTile(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.FadeOverflow(), _note);
    }

    protected override void OnDispose() => _watch.NeedsYou -= OnNeedsYou;

    void OnNeedsYou(TaskItem item) => ToolKit.OnUi(() => _host.Toast(item.State switch
    {
        "waiting" => L.F("{0} is waiting for you", item.Title),
        "failed" => L.F("{0} failed", item.Title),
        _ => L.F("{0} finished", item.Title),
    }));

    public override void Update(Snapshot s)
    {
        var items = _watch.State;
        string signature = string.Join("|", items.Select(i => $"{i.Id}:{i.State}:{i.Title}:{i.Detail}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(items);
        }
        var now = s.Time.ToUniversalTime();
        foreach (var (item, time) in _times)
            time.Text = item.State == "running" ? W.Duration(Positive(now - item.Started)) : NotificationText.Ago(item.Changed.ToLocalTime(), s.Time);
    }

    static TimeSpan Positive(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    void Rebuild(IReadOnlyList<TaskItem> items)
    {
        int waiting = items.Count(i => i.State == "waiting"), running = items.Count(i => i.State == "running");
        _note.Text = waiting > 0 ? L.F("{0} waiting for you", waiting) : running > 0 ? L.F("{0} running", running) : "";
        _note.Foreground = waiting > 0 ? Theme.Warn : Theme.Dim;

        _rows.Children.Clear();
        _times.Clear();
        if (items.Count == 0)
        {
            _rows.Children.Add(W.Empty(ToolKit.Terminal, L.T(ToolKit.EmptyTasks)));
            return;
        }
        foreach (var item in items)
        {
            var (glyph, color) = item.State switch
            {
                "waiting" => (ToolKit.Waiting, Theme.Warn),
                "failed" => (ToolKit.Failed, Theme.Crit),
                "done" => (ToolKit.Ok, Theme.Good),
                _ => (ToolKit.Running, Theme.Accent),
            };
            var title = W.Text(item.Title, Theme.Small + 1, Theme.Text, ToolKit.Semi);
            var time = W.Text("", Theme.Small - 1, Theme.Dim);
            _times.Add((item, time));

            var took = Positive(item.Changed - item.Started);
            string state = item.State switch
            {
                "waiting" => L.T("Waiting for you"),
                "failed" => item.ExitCode is int code ? L.F("Failed (exit {0}) after {1}", code, W.Duration(took)) : L.T("Failed"),
                "done" => item.Kind == "agent" ? L.T("Finished · your turn") : L.F("Done in {0}", W.Duration(took)),
                _ => item.Kind == "agent" ? L.T("Working") : L.T("Running"),
            };
            var line = W.Text(string.IsNullOrWhiteSpace(item.Detail) ? state : $"{state} · {item.Detail}", Theme.Small, item.State == "running" ? Theme.Dim : color);
            line.TextWrapping = TextWrapping.Wrap;
            line.TextTrimming = TextTrimming.None;

            var row = ToolKit.Row(ToolKit.Glyph(glyph, Theme.Small + 7, color), W.Stack(ToolKit.Head(title, time), line), color, item.State == "waiting");
            // A tap takes a finished one off the list; running ones go by themselves.
            if (item.NeedsYou)
                Tap.On(row, () =>
                {
                    _watch.Dismiss(item);
                    _signature = "?";
                });
            _rows.Children.Add(row);
        }
    }
}

/// <summary>
/// Electricity price (Norway): what a kWh costs this hour, when the cheapest stretch ahead starts, and the day as
/// bars with the cheap hours in green and the dear ones in amber (Sensors/PowerPrice.cs).
/// </summary>
public sealed class PowerPriceModule : Module
{
    readonly PowerPriceService _service;
    readonly TextBlock _now, _cheap, _cheapCaption, _note, _range;
    readonly UniformGrid _bars = new() { Rows = 1, MinHeight = 26, Margin = new Thickness(0, 8, 0, 0) };
    readonly double _vat, _hours;
    PowerPrices? _shown;
    DateTime _shownPeriod;

    public PowerPriceModule(ModuleConfig config) : base(config, "Electricity price")
    {
        string area = PowerPriceService.Areas.FirstOrDefault(a => a.Equals(config.Opt("Area")?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "NO1";
        _vat = double.TryParse(config.Opt("Vat")?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vat) ? Math.Clamp(vat, 0, 100) : 0;
        _hours = Math.Clamp(config.OptInt("Hours", 3), 1, 12);
        _service = ServiceStore.Get("powerprice:" + area, () => new PowerPriceService(area));
        _note = W.Text(area + (_vat > 0 ? L.T(" · incl. VAT") : L.T(" · spot price")), Theme.Small, Theme.Dim);
        var stats = W.Columns(W.Stat("kr/kWh now", out _now), W.Stat("", out _cheap, out _cheapCaption));
        _range = W.Text("", Theme.Small, Theme.Dim);
        _range.Margin = new Thickness(0, 4, 0, 0);
        _now.Text = _cheap.Text = "–";
        _cheapCaption.Text = L.F("cheapest {0} h start", _hours);
        View = MakeTile(Body(_bars, stats, _range.ShowFrom(Density.Normal)), _note);
    }

    double WithVat(double price) => price * (1 + _vat / 100);

    public override void Update(Snapshot s)
    {
        var prices = _service.State;
        if (prices == null)
        {
            if (_service.Error != null)
                _range.Text = L.T("Can't reach the price feed");
            return;
        }
        var current = prices.At(s.Time);
        // New numbers twice an hour; the picture only changes when they do or the hour turns.
        if (ReferenceEquals(prices, _shown) && current?.Start == _shownPeriod)
            return;
        _shown = prices;
        _shownPeriod = current?.Start ?? default;

        var today = prices.Points.Where(p => p.Start.Date == s.Time.Date).ToList();
        double low = prices.Points.Count > 0 ? prices.Points.Min(p => p.Price) : 0, high = prices.Points.Count > 0 ? prices.Points.Max(p => p.Price) : 1;
        double span = Math.Max(0.01, high - low);
        _now.Text = current == null ? "–" : WithVat(current.Price).ToString("0.00", L.Culture);
        // Dear compared with the rest of what is known (today, and tomorrow once it is out).
        _now.Foreground = current == null ? Theme.Text : current.Price >= low + span * 0.67 ? Theme.Warn : current.Price <= low + span * 0.33 ? Theme.Good : Theme.Text;
        if (prices.Cheapest(s.Time, _hours) is { } best)
        {
            bool running = best.Start <= s.Time;
            _cheap.Text = running ? L.T("now") : best.Start.ToString("HH:mm");
            _cheap.Foreground = running ? Theme.Good : Theme.Text;
            string price = WithVat(best.Average).ToString("0.00", L.Culture);
            _cheapCaption.Text = best.Start.Date > s.Time.Date ? L.F("tomorrow · cheapest {0} h · {1} kr", _hours, price) : L.F("cheapest {0} h · {1} kr", _hours, price);
        }
        else
        {
            _cheap.Text = "–";
            _cheapCaption.Text = L.F("cheapest {0} h start", _hours);
        }
        bool tomorrow = prices.Points.Any(p => p.Start.Date > s.Time.Date);
        _range.Text = today.Count == 0 ? "" : L.F("today {0}–{1} kr", WithVat(today.Min(p => p.Price)).ToString("0.00", L.Culture), WithVat(today.Max(p => p.Price)).ToString("0.00", L.Culture))
            + (tomorrow ? L.T(" · tomorrow is in") : "");

        _bars.Children.Clear();
        foreach (var p in prices.Points)
        {
            double share = Math.Clamp(0.12 + 0.88 * (p.Price - low) / span, 0.05, 1);
            bool isNow = p == current, past = p.End <= s.Time;
            var color = isNow ? Theme.Accent : p.Price <= low + span * 0.33 ? Theme.Good : p.Price >= low + span * 0.67 ? Theme.Warn : Theme.Dim;
            var cell = new Grid();
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - share, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(share, GridUnitType.Star) });
            var bar = new Border { Background = past ? ToolKit.Tint(color, 0x48) : color, Margin = new Thickness(0.5, 0, 0.5, 0), CornerRadius = new CornerRadius(ToolKit.Square ? 0 : 1.5) };
            Grid.SetRow(bar, 1);
            cell.Children.Add(bar);
            _bars.Children.Add(cell);
        }
    }
}
