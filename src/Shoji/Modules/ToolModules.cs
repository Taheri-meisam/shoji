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
    // Segoe Fluent Icons / MDL2: Completed, Sync, TouchPointer, ErrorBadge, CommandPrompt, Component.
    public const string Ok = "\uE930", Running = "\uE895", Waiting = "\uE7C9", Failed = "\uEA39", Terminal = "\uE756", Chip = "\uE950";
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontWeight Semi = FontWeights.SemiBold;
    public static bool Square => Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
    public static Paint Tint(Paint brush, byte alpha) => Theme.Freeze(new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)brush).Color, alpha)));
    public static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
    public static TaskWatch NewWatch() => new TaskWatch();
    public const string EmptyTasks = "Nothing running. Coding agents and reported tasks show up here.";

    /// <summary>A bar split into parts by their share (the last part is usually "the rest").</summary>
    public static Grid SplitBar(double height, params (double Share, Paint Brush)[] parts)
    {
        var bar = new Grid { Height = height };
        int column = 0;
        foreach (var (share, brush) in parts)
        {
            if (share <= 0)
                continue;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            var part = new Border { Background = brush, Margin = new Thickness(0, 0, 1.5, 0), CornerRadius = new CornerRadius(Square ? 0 : height / 2) };
            Grid.SetColumn(part, column++);
            bar.Children.Add(part);
        }
        return bar;
    }

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

    void OnNeedsYou(TaskItem item)
    {
        // The dashboard's own card says it (and stays while something waits); the toast is for when that is off.
        if (!TaskCard.Enabled)
            ToolKit.OnUi(() => _host.Toast(TaskCard.Title(item)));
    }

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

/// <summary>
/// Local models: what a model server on this PC (Ollama, LM Studio, llama.cpp…) has in memory, how much of it sits
/// on the graphics card, when it will be unloaded, and what else is on disk (Sensors/LocalModels.cs).
/// </summary>
public sealed class LocalModelsModule : Module
{
    readonly LocalModelsService _service;
    readonly StackPanel _rows = new();
    readonly StackPanel _memory = new() { Margin = new Thickness(0, 0, 0, 9) };
    readonly TextBlock _note;
    readonly List<(LocalModel Model, TextBlock Time)> _times = new();
    string _signature = "?";

    public LocalModelsModule(ModuleConfig config) : base(config, "Local models")
    {
        string? server = config.Opt("Server");
        _service = ServiceStore.Get(LocalModelsService.KeyFor(server), () => new LocalModelsService(server));
        _note = W.Text("", Theme.Small, Theme.Dim);
        View = MakeTile(Body(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.FadeOverflow(), _memory), _note);
    }

    public override void Update(Snapshot s)
    {
        var state = _service.State;
        if (state == null)
            return;
        double used = s.Gpu?.VramUsedMb ?? 0, total = s.Gpu?.VramTotalMb ?? 0;
        // The list is rebuilt when the models change; the memory bar when it moved by a noticeable step.
        string signature = (state.Reachable ? state.Server : "-") + "|" + string.Join("|", state.Models.Select(m => $"{m.Name}:{m.Loaded}:{m.GpuBytes}:{m.Context}"))
            + "|" + Math.Round(used / 100) + "/" + Math.Round(total / 100) + "|" + Math.Round((s.Gpu?.LoadPercent ?? 0) / 5);
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(state, s);
        }
        var now = s.Time.ToUniversalTime();
        foreach (var (model, time) in _times)
            time.Text = LocalModelsText.Unloads(model.Unloads, now);
    }

    void Rebuild(LocalModels state, Snapshot s)
    {
        _rows.Children.Clear();
        _memory.Children.Clear();
        _times.Clear();
        // Biggest first: the server lists them in whatever order they were last used.
        var loaded = state.Loaded.OrderByDescending(m => m.Bytes).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        var onDisk = state.OnDisk.ToList();
        _note.Text = !state.Reachable ? "" : loaded.Count > 0 ? L.F("{0} · {1} loaded", state.Server, loaded.Count) : state.Server;

        if (!state.Reachable)
        {
            _rows.Children.Add(W.Empty(ToolKit.Chip, L.T("No model server is running. Start Ollama, or give another server's address in this tile's options.")));
            return;
        }
        if (loaded.Count == 0)
            _rows.Children.Add(W.Empty(ToolKit.Chip, onDisk.Count > 0 ? L.F("No model in memory. {0} on disk, ready to load.", onDisk.Count) : L.T("No model in memory.")));

        foreach (var m in loaded)
        {
            // Partly in normal memory is the slow case: say so in the warning colour.
            bool spilled = m.Bytes > 0 && m.GpuBytes < m.Bytes * 0.98;
            var color = spilled ? Theme.Warn : Theme.Good;
            var title = W.Text(LocalModelsText.Name(m.Name), Theme.Small + 1, Theme.Text, ToolKit.Semi);
            var time = W.Text("", Theme.Small - 1, Theme.Dim);
            _times.Add((m, time));
            string where = m.Bytes <= 0 ? ""
                : !spilled ? L.F("{0} on the graphics card", LocalModelsText.Size(m.Bytes))
                : m.GpuBytes <= 0 ? L.F("{0} in normal memory (slow)", LocalModelsText.Size(m.Bytes))
                : L.F("{0}, {1:0}% on the graphics card", LocalModelsText.Size(m.Bytes), 100.0 * m.GpuBytes / m.Bytes);
            string text = string.Join(" · ", new[] { m.Detail, where, m.Context is int c ? L.F("context {0}", LocalModelsText.Context(c)) : null }.Where(p => !string.IsNullOrEmpty(p)));
            var line = W.Text(text, Theme.Small, spilled ? Theme.Warn : Theme.Dim);
            line.TextWrapping = TextWrapping.Wrap;
            line.TextTrimming = TextTrimming.None;
            _rows.Children.Add(ToolKit.Row(ToolKit.Glyph(ToolKit.Chip, Theme.Small + 7, color), W.Stack(ToolKit.Head(title, time), line), color, false));
        }

        if (onDisk.Count > 0)
        {
            const int show = 6;
            string names = string.Join(", ", onDisk.Take(show).Select(m => LocalModelsText.Name(m.Name)));
            var disk = W.Text(L.F("On disk: {0}", onDisk.Count > show ? names + " +" + (onDisk.Count - show) : names), Theme.Small, Theme.Dim);
            disk.TextWrapping = TextWrapping.Wrap;
            disk.TextTrimming = TextTrimming.None;
            disk.Margin = new Thickness(2, 2, 0, 0);
            _rows.Children.Add(disk);
        }

        // Graphics memory: these models, everything else on the card, and what is free.
        if (s.Gpu is { VramUsedMb: double used, VramTotalMb: double total } gpu && total > 0)
        {
            double models = Math.Min(used, loaded.Sum(m => m.GpuBytes) / 1048576.0);
            var caption = W.Text(L.T("Graphics memory"), Theme.Small, Theme.Dim);
            string load = gpu.LoadPercent is double percent ? " · " + L.F("load {0:0}%", percent) : "";
            var value = W.Text($"{used / 1024:0.0} / {total / 1024:0.0} GB{load}", Theme.Small, Theme.Text);
            _memory.Children.Add(ToolKit.Head(caption, value));
            var bar = ToolKit.SplitBar(6, (models, Theme.Good), (used - models, Theme.Dim), (Math.Max(0, total - used), Theme.Track));
            bar.Margin = new Thickness(0, 4, 0, 0);
            _memory.Children.Add(bar);
        }
    }
}
