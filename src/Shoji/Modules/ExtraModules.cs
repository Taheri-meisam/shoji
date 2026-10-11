using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Auto-discovered temperatures or fans, one row with a bar each.</summary>
public sealed class ReadingsModule : Module
{
    readonly RowList _list = new(withBar: true);
    readonly Func<Snapshot, IReadOnlyList<SensorReading>> _select;
    readonly bool _temperature;
    readonly TextBlock _summary, _note;
    double _maxRpm = 2000;

    public ReadingsModule(ModuleConfig config, string title, Func<Snapshot, IReadOnlyList<SensorReading>> select, bool temperature)
        : base(config, title)
    {
        _select = select;
        _temperature = temperature;
        _summary = W.Text("", Theme.Small, Theme.Dim);
        _note = W.Text("", Theme.Small - 1, Theme.Dim);
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 4, 0, 0);
        DockPanel.SetDock(_note, Dock.Bottom);

        var body = new DockPanel();
        body.Children.Add(_note);
        body.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        View = MakeTile(body, _summary);
    }

    public override void Update(Snapshot s)
    {
        var items = _select(s);
        for (int i = 0; i < items.Count; i++)
        {
            var r = items[i];
            if (_temperature)
            {
                var color = r.Value >= Theme.TempCrit ? Theme.Crit : r.Value >= Theme.TempWarn ? Theme.Warn : Theme.Accent;
                _list.Set(i, r.Label, W.Temp(r.Value, unit: true), r.Value / (Theme.TempCrit + 10) * 100,
                          Theme.ForTemperature(r.Value), color);
            }
            else
            {
                _maxRpm = Math.Max(_maxRpm, r.Value);
                _list.Set(i, r.Label, r.Value < 1 ? L.T("stopped") : $"{r.Value:0} rpm", r.Value / _maxRpm * 100,
                          r.Value < 1 ? Theme.Dim : null, Theme.Accent);
            }
        }
        _list.Trim(items.Count);

        _summary.Text = items.Count == 0 ? "" : _temperature ? L.F("max {0}", W.Temp(items.Max(r => r.Value))) : L.F("{0} found", items.Count);

        // Without the driver only GPU, drives and USB coolers report; say so instead of looking broken.
        _note.Text = HardwareSensors.DriverReady
            ? items.Count == 0 ? (_temperature ? L.T("No temperature sensors found.") : L.T("No fan has reported any speed yet.")) : ""
            : L.F("CPU and motherboard sensors {0}: tray icon → Enable CPU temperature.", HardwareSensors.DriverHint);
    }
}

/// <summary>Static specs: Windows version, CPU, GPU, RAM, motherboard…</summary>
public sealed class SystemModule : Module
{
    readonly StackPanel _rows = new();
    readonly TextBlock _host, _uptime;
    readonly Dictionary<string, TextBlock> _values = new();

    public SystemModule(ModuleConfig config) : base(config, "System")
    {
        _host = W.Text("", Theme.Small, Theme.Dim);
        foreach (var label in new[] { "Windows", "Build", "CPU", "Cores", "GPU", "Driver", "RAM", "Board", "BIOS", "Screens" })
            _rows.Children.Add(SpecRow(label));
        _rows.Children.Add(SpecRow("Up since"));
        _uptime = _values["Up since"];
        View = MakeTile(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, _host);
    }

    /// <summary>Label keeps its width; long values (CPU names) trim from the right instead of pushing the label out.</summary>
    Grid SpecRow(string label)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var value = W.Text("–");
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 1);
        g.Children.Add(W.Text(L.T(label), Theme.Small, Theme.Dim));
        g.Children.Add(value);
        _values[label] = value;
        return g;
    }

    public override void Update(Snapshot s)
    {
        var i = s.System;
        if (i == null)
        {
            _values["Windows"].Text = L.T("reading…");
            return;
        }

        _host.Text = i.Host;
        Set("Windows", i.Os);
        Set("Build", i.OsBuild);
        Set("CPU", i.Cpu);
        Set("Cores", i.CpuCores);
        Set("GPU", i.Gpu);
        Set("Driver", i.GpuDriver);
        Set("RAM", i.Ram);
        Set("Board", i.Board);
        Set("BIOS", i.Bios);
        Set("Screens", i.Displays);
        _uptime.Text = $"{i.BootTime.ToString("ddd HH:mm", L.Culture)} ({W.Duration(s.Uptime)})";
    }

    void Set(string label, string value)
    {
        var tb = _values[label];
        tb.Text = string.IsNullOrWhiteSpace(value) ? "–" : value;
        tb.ToolTip = tb.Text;
    }
}

/// <summary>Current weather and the next three days.</summary>
public sealed class WeatherModule : Module
{
    static readonly CultureInfo Japanese = new("ja-JP");

    readonly TextBlock _place, _temp, _desc, _feels, _humidity, _wind, _rain;
    readonly WeatherArt _icon;
    readonly UniformGrid _days = new() { Rows = 1 };
    readonly bool _kanji = Theme.Current.Japanese;
    DateTime _shownUpdate;

    public WeatherModule(ModuleConfig config) : base(config, "Weather")
    {
        _place = W.Text("", Theme.Small, Theme.Dim);
        _icon = new WeatherArt(58);
        _icon.Margin = new Thickness(0, 0, 14, 0);
        _temp = W.Number("–", Theme.Big + 6);
        _desc = W.Text(L.T("Loading…"), Theme.Medium, Theme.Dim);
        _desc.TextWrapping = TextWrapping.Wrap;

        var now = new DockPanel();
        DockPanel.SetDock(_icon, Dock.Left);
        now.Children.Add(_icon);
        now.Children.Add(W.Stack(_temp, _desc));

        var details = W.Stack(
            W.SpacedColumns(W.Row(L.T("Feels like"), out _feels), W.Row(L.T("Humidity"), out _humidity)),
            W.SpacedColumns(W.Row(L.T("Wind"), out _wind), W.Row(L.T("Rain"), out _rain)));
        details.Margin = new Thickness(0, 8, 0, 4);

        _days.Margin = new Thickness(0, 6, 0, 0);
        View = MakeTile(Body(_days.ShowFrom(Density.Normal), now, details.ShowFrom(Density.Normal)), _place);
    }

    internal static TextBlock WeatherIcon(double size)
    {
        var t = W.Text("", size, Theme.Accent);
        // Japanese themes use a single kanji (晴 曇 雨 雪) instead of a symbol.
        t.FontFamily = Theme.Current.Japanese ? Theme.TitleFont : new FontFamily("Segoe UI Symbol, Segoe UI Emoji");
        t.Effect = Theme.NumberEffect;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    public override void Update(Snapshot s)
    {
        var state = s.Weather;
        if (state?.Info is not { } w)
        {
            _desc.Text = state?.Message ?? L.T("Loading…");
            return;
        }
        if (w.UpdatedAt == _shownUpdate)
            return; // only changes every 15 minutes
        _shownUpdate = w.UpdatedAt;

        var (text, kanji, glyph) = WeatherService.Describe(w.Code, w.IsDay);
        text = L.T(text);
        _place.Text = w.Place;
        _icon.Set(w.Code, w.IsDay);
        _temp.Text = W.Temp(w.TempC);
        _desc.Text = _kanji ? $"{kanji}  {text}" : text; // Japanese themes keep their kanji next to the picture
        _feels.Text = W.Temp(w.FeelsLikeC);
        _humidity.Text = $"{w.Humidity}%";
        _wind.Text = Theme.Fahrenheit ? $"{w.WindKmh * 0.621:0} mph" : $"{w.WindKmh:0} km/h";
        _rain.Text = w.Days.Count > 0 && w.Days[0].RainChance is int rc ? $"{rc}%" : "–";

        _days.Children.Clear();
        foreach (var d in w.Days.Skip(1).Take(3))
        {
            var icon = new WeatherArt(30) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            icon.Set(d.Code);
            var name = W.Text(_kanji ? d.Date.ToString("ddd", Japanese) + "曜" : d.Date.ToString("ddd", L.Culture).TrimEnd('.'), Theme.Small, Theme.Dim);
            var range = W.Text($"{W.Temp(d.MaxC)} / {W.Temp(d.MinC)}", Theme.Small);
            name.HorizontalAlignment = range.HorizontalAlignment = HorizontalAlignment.Center;
            _days.Children.Add(W.Stack(name, icon, range));
        }
    }
}

/// <summary>Hourly forecast: temperature curve with an icon per hour, rain-chance bars and hour labels.</summary>
public sealed class ForecastModule : Module
{
    readonly HourlyChart _chart;
    readonly TextBlock _place, _info = InfoLine();
    DateTime _shown;

    public ForecastModule(ModuleConfig config, bool use24Hour) : base(config, "Next hours")
    {
        _chart = new HourlyChart(Math.Clamp(config.OptInt("Hours", 12), 6, 24), use24Hour);
        _place = W.Text("", Theme.Small, Theme.Dim);
        View = GaugeTile(_chart, _info, _place);
    }

    public override void Update(Snapshot s)
    {
        var state = s.Weather;
        if (state?.Info is not { } w || w.Hours.Count == 0)
        {
            _info.Text = state?.Message ?? L.T("Loading forecast…");
            return;
        }
        if (w.UpdatedAt == _shown)
            return;
        _shown = w.UpdatedAt;
        _place.Text = w.Place;
        _chart.Set(w.Hours);

        // One plain-language line: when rain is coming, or that it stays dry; plus the range.
        var hours = w.Hours.Take(_chart.Count).ToList();
        var wet = hours.FirstOrDefault(h => h.RainChance >= 50);
        string rain = wet == null
            ? L.F("Dry for the next {0} hours", hours.Count)
            : wet == hours[0] ? L.F("Rain likely now ({0}%)", wet.RainChance) : L.F("Rain likely from {0:HH:mm} ({1}%)", wet.Time, wet.RainChance);
        _info.Text = L.F("{0}  ·  {1} to {2}", rain, W.Temp(hours.Min(h => h.TempC)), W.Temp(hours.Max(h => h.TempC)));
    }
}

/// <summary>Draws the hourly forecast. Static (redrawn when a new forecast arrives, every 15 minutes).</summary>
public sealed class HourlyChart : FrameworkElement
{
    readonly bool _use24Hour;
    IReadOnlyList<WeatherHour> _hours = Array.Empty<WeatherHour>();
    public int Count { get; }

    public HourlyChart(int count, bool use24Hour)
    {
        Count = count;
        _use24Hour = use24Hour;
        MinHeight = 80;
    }

    public void Set(IReadOnlyList<WeatherHour> hours)
    {
        _hours = hours.Take(Count).ToList();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _hours.Count;
        double w = ActualWidth, h = ActualHeight;
        if (n < 2 || w < 60 || h < 70)
            return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double col = w / n;
        int every = col >= 30 ? 1 : col >= 16 ? 2 : 3;   // label every hour when there's room
        bool jp = Theme.Current.Japanese;

        // Rows: icons, temperature labels + curve, rain bars, hour labels.
        double iconY = 12, curveTop = 38, hourY = h - 8, rainBottom = h - 20, rainH = Math.Min(26, h * 0.18), curveBottom = rainBottom - rainH - 10;
        if (curveBottom - curveTop < 20)
            curveTop = Math.Max(26, curveBottom - 20);
        double min = _hours.Min(x => x.TempC), max = _hours.Max(x => x.TempC), span = Math.Max(3, max - min);
        Point P(int i) => new(col * (i + 0.5), curveBottom - (_hours[i].TempC - min) / span * (curveBottom - curveTop));

        var face = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var iconFace = new Typeface(jp ? Theme.TitleFont : new FontFamily("Segoe UI Symbol, Segoe UI Emoji"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        void Label(string text, double x, double y, double size, Brush brush, Typeface tf)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, w - ft.Width)), y - ft.Height / 2));
        }

        // Faint "now" column.
        dc.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.AccentColor, 0x14)), null, new Rect(0, 0, col, h));

        // Curve with a soft fill beneath.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(P(0), false, false);
            a.BeginFigure(new Point(P(0).X, curveBottom + 4), true, true);
            a.LineTo(P(0), false, false);
            for (int i = 1; i < n; i++)
            {
                // Smooth: a cubic through the midpoints between hours.
                var p0 = P(i - 1);
                var p1 = P(i);
                var c1 = new Point((p0.X + p1.X) / 2, p0.Y);
                var c2 = new Point((p0.X + p1.X) / 2, p1.Y);
                l.BezierTo(c1, c2, p1, true, true);
                a.BezierTo(c1, c2, p1, false, false);
            }
            a.LineTo(new Point(P(n - 1).X, curveBottom + 4), false, false);
        }
        line.Freeze();
        area.Freeze();
        var fill = new LinearGradientBrush(Theme.WithAlpha(Theme.AccentColor, Theme.Current.GraphFill), Theme.WithAlpha(Theme.AccentColor, 0), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        if (Theme.Current.GraphGlow)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Theme.WithAlpha(Theme.AccentColor, 0x40)), 6), line);
        dc.DrawGeometry(null, new Pen(Theme.Accent, 2) { LineJoin = PenLineJoin.Round }, line);

        for (int i = 0; i < n; i++)
        {
            var hr = _hours[i];
            var p = P(i);
            bool labelled = i % every == 0;
            dc.DrawEllipse(labelled ? Theme.Accent : Theme.Track, null, p, labelled ? 2.6 : 1.6, labelled ? 2.6 : 1.6);
            double x = col * (i + 0.5);

            if (labelled)
            {
                var (_, kanji, glyph) = WeatherService.Describe(hr.Code, hr.IsDay);
                Label(jp ? kanji : glyph, x, iconY, Math.Min(18, Math.Max(11, col * 0.6)), Theme.Accent, iconFace);
                Label(W.Temp(hr.TempC), x, p.Y - 12, Theme.Small, Theme.Text, face);
                string hour = i == 0 ? (jp ? "今" : L.T("Now")) : _use24Hour ? hr.Time.ToString("HH") : hr.Time.ToString("h tt").ToLowerInvariant().Replace(" ", "");
                Label(hour, x, hourY, Theme.Small - 1.5, i == 0 ? Theme.Accent : Theme.Dim, face);
            }

            // Rain chance bar (with the % when it matters).
            if (hr.RainChance is int rc && rc > 0)
            {
                double bh = rainH * rc / 100.0, bw = Math.Max(2, col * 0.5);
                dc.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.Accent2Color, (byte)(0x50 + rc * 1.5))), null, new Rect(x - bw / 2, rainBottom - bh, bw, bh));
                if (labelled && rc >= 30)
                    Label($"{rc}%", x, rainBottom - bh - 7, Theme.Small - 2.5, Theme.Accent2, face);
            }
        }
        dc.DrawLine(new Pen(Theme.Track, 1), new Point(0, rainBottom + 0.5), new Point(w, rainBottom + 0.5));
    }
}

/// <summary>Compact current weather for the clock: icon (or kanji), temperature, conditions with today's high / low.</summary>
sealed class WeatherBadge : StackPanel
{
    readonly TextBlock _temp, _desc;
    readonly WeatherArt _icon;
    DateTime _shown;

    public WeatherBadge(double scale = 1)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        _icon = new WeatherArt(40 * scale) { VerticalAlignment = VerticalAlignment.Center };
        _icon.Margin = new Thickness(0, 0, 10 * scale, 0);
        _temp = W.Number("", 30 * scale);
        _temp.Margin = new Thickness(0, -4, 0, -2);
        _desc = W.Text("", Theme.Small, Theme.Dim);
        Children.Add(_icon);
        Children.Add(W.Stack(_temp, _desc.ShowFrom(Density.Normal)));
    }

    public void Update(WeatherState? state)
    {
        if (state?.Info is not { } w)
        {
            // No forecast yet: only speak up when the user has to do something (set a city).
            bool needsCity = state?.Message is { } message && (message.Contains("Settings") || message.Contains(L.T("Settings")));
            Visibility = needsCity ? Visibility.Visible : Visibility.Collapsed;
            _temp.Text = "";
            _desc.Text = needsCity ? L.T("Weather: set a city in Settings") : "";
            return;
        }
        Visibility = Visibility.Visible;
        if (w.UpdatedAt == _shown)
            return; // changes every 15 minutes
        _shown = w.UpdatedAt;
        var (text, kanji, glyph) = WeatherService.Describe(w.Code, w.IsDay);
        text = L.T(text);
        _icon.Set(w.Code, w.IsDay);
        _temp.Text = W.Temp(w.TempC);
        if (Theme.Current.Japanese)
            text = $"{kanji} {text}";
        _desc.Text = w.Days.Count > 0 ? L.F("{0} · H {1} L {2}", text, W.Temp(w.Days[0].MaxC), W.Temp(w.Days[0].MinC)) : text;
        ToolTip = L.F("{0} · feels like {1} · updated {2:t}", w.Place, W.Temp(w.FeelsLikeC), w.UpdatedAt);
    }

    /// <summary>One line for small spaces: "⛅ 18° Partly cloudy".</summary>
    public static string Line(WeatherState? state)
    {
        if (state?.Info is not { } w)
            return "";
        var (text, kanji, glyph) = WeatherService.Describe(w.Code, w.IsDay);
        return $"{(Theme.Current.Japanese ? kanji : glyph)} {W.Temp(w.TempC)} {L.T(text)}";
    }
}

/// <summary>Latency to a host, with a history graph.</summary>
public sealed class PingModule : Module
{
    readonly TextBlock _last, _lastCaption, _loss, _avg, _jitter;
    readonly Sparkline _history = new("ping", fixedMax: null);

    public PingModule(ModuleConfig config) : base(config, "Ping")
    {
        var stats = W.Columns(W.Stat("ms", out _last, out _lastCaption), W.Stat(L.T("packet loss"), out _loss));
        var details = W.SpacedColumns(W.Row(L.T("Average"), out _avg), W.Row(L.T("Jitter"), out _jitter));
        details.Margin = new Thickness(0, 6, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, details.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var p = s.Ping;
        if (p == null)
            return;

        _lastCaption.Text = "ms · " + p.Host;
        _last.Text = p.LastMs is double ms ? ms.ToString("0") : L.T("timeout");
        _last.Foreground = p.LastMs is null or >= 250 ? Theme.Crit : p.LastMs >= 100 ? Theme.Warn : Theme.Text;
        _loss.Text = $"{p.LossPercent:0}%";
        _loss.Foreground = p.LossPercent >= 5 ? Theme.Crit : p.LossPercent > 0 ? Theme.Warn : Theme.Text;
        _avg.Text = W.Format(p.AverageMs, "0", " ms");
        _jitter.Text = W.Format(p.JitterMs, "0.0", " ms");
        _history.Refresh();
    }
}

/// <summary>This month, with today highlighted and a dot under days that have events.</summary>
public sealed class CalendarModule : Module
{
    static readonly CultureInfo Japanese = new("ja-JP");

    readonly UniformGrid _grid = new() { Columns = 7 };
    readonly TextBlock _month;
    readonly AgendaService? _agenda;
    DateTime _shownDay;
    object? _shownEvents;

    /// <param name="agenda">The calendars from Settings, for the event dots (null = none set).</param>
    public CalendarModule(ModuleConfig config, AgendaService? agenda = null) : base(config, "Calendar")
    {
        _agenda = agenda;
        _month = W.Text("", Theme.Small, Theme.Dim);
        View = MakeTile(_grid, _month);
    }

    public override void Update(Snapshot s)
    {
        var today = s.Time.Date;
        // Each fetch hands over a new list, so this redraws on a new day or when the events changed.
        var events = _agenda?.State;
        if (today == _shownDay && ReferenceEquals(events, _shownEvents))
            return;
        _shownDay = today;
        _shownEvents = events;
        var marks = CalendarFeeds.Marks(events, today.Year, today.Month);

        bool jp = Theme.Current.Japanese;
        _month.Text = jp ? today.ToString("yyyy年M月", Japanese) : today.ToString(L.T("MMMM yyyy"), L.Culture);
        _grid.Children.Clear();

        var culture = L.Culture; // day names and the first day of the week in the chosen language
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)firstDay + i) % 7);
            string name = jp ? "日月火水木金土"[(int)dow].ToString() : culture.DateTimeFormat.GetShortestDayName(dow);
            var h = Cell(name, DayColor(dow, jp) ?? Theme.Dim, Theme.Small);
            if (jp)
                h.FontFamily = Theme.TitleFont;
            _grid.Children.Add(h);
        }

        var first = new DateTime(today.Year, today.Month, 1);
        int lead = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        for (int i = 0; i < lead; i++)
            _grid.Children.Add(new Border());

        int days = DateTime.DaysInMonth(today.Year, today.Month);
        for (int d = 1; d <= days; d++)
        {
            var date = first.AddDays(d - 1);
            marks.TryGetValue(d, out var calendars);
            if (date == today)
            {
                var mark = Cell(d.ToString(), Theme.Background, Theme.Small + 1);
                mark.FontWeight = FontWeights.SemiBold;
                _grid.Children.Add(new Border
                {
                    Background = Theme.Accent,
                    CornerRadius = new CornerRadius(Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer ? 0 : 6),
                    Margin = new Thickness(2),
                    Child = WithDots(mark, calendars, Theme.Background),
                });
            }
            else
            {
                _grid.Children.Add(WithDots(Cell(d.ToString(), date < today ? Theme.Dim : DayColor(date.DayOfWeek, jp) ?? Theme.Text, Theme.Small + 1), calendars, null));
            }
        }
    }

    /// <summary>The day number with up to three small dots under it, one per calendar with something on that day.</summary>
    UIElement WithDots(TextBlock number, List<int>? calendars, Brush? color)
    {
        if (calendars == null)
            return number;
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
        foreach (int c in calendars)
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Margin = new Thickness(1, 0, 1, 0), Fill = color ?? AgendaModule.CalendarBrush(_agenda, c) });
        return new Grid { Children = { number, dots } };
    }

    // Japanese calendars print Sundays in red and Saturdays in blue.
    static Brush? DayColor(DayOfWeek d, bool jp) =>
        !jp ? null : d == DayOfWeek.Sunday ? Theme.Accent : d == DayOfWeek.Saturday ? Theme.Accent2 : null;

    static TextBlock Cell(string text, Brush color, double size)
    {
        var t = W.Text(text, size, color);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextTrimming = TextTrimming.None;
        return t;
    }
}

/// <summary>Current time in other time zones.</summary>
public sealed class WorldClockModule : Module
{
    public static readonly ZoneRef[] Defaults =
    {
        new() { Zone = "Tokyo Standard Time", Label = "Tokyo" },
        new() { Zone = "GMT Standard Time", Label = "London" },
        new() { Zone = "Eastern Standard Time", Label = "New York" },
        new() { Zone = "Pacific Standard Time", Label = "Los Angeles" },
    };

    readonly List<(TimeZoneInfo? Zone, TextBlock Time, TextBlock Sub)> _rows = new();
    readonly bool _use24Hour;

    public WorldClockModule(ModuleConfig config, bool use24Hour) : base(config, "World clock")
    {
        _use24Hour = use24Hour;
        var grid = new UniformGrid { Columns = 1 };
        foreach (var z in config.Zones is { Count: > 0 } zones ? zones : Defaults.ToList())
        {
            var tz = Resolve(z.Zone);
            var label = W.Text(z.Label ?? NameOf(z.Zone), Theme.Medium);
            var sub = W.Text(tz == null ? L.T("unknown time zone") : "", Theme.Small, Theme.Dim);
            var time = W.Number("–", Theme.Medium + 9);
            time.HorizontalAlignment = HorizontalAlignment.Right;

            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(W.Stack(label, sub));
            grid.Children.Add(row);
            _rows.Add((tz, time, sub));
        }
        View = MakeTile(grid);
    }

    static TimeZoneInfo? Resolve(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)
                ? TimeZoneInfo.FindSystemTimeZoneById(win)
                : null;
        }
    }

    // "Asia/Tokyo" → "Tokyo", "Tokyo Standard Time" → "Tokyo"
    static string NameOf(string id) =>
        id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ') : id.Replace(" Standard Time", "");

    public override void Update(Snapshot s)
    {
        foreach (var (tz, time, sub) in _rows)
        {
            if (tz == null)
                continue;
            var there = TimeZoneInfo.ConvertTime(s.Time, TimeZoneInfo.Local, tz);
            time.Text = there.ToString(_use24Hour ? "HH:mm" : "h:mm tt");

            double hours = (there - s.Time).TotalHours;
            string offset = Math.Abs(hours) < 0.01 ? L.T("same time") : $"{(hours > 0 ? "+" : "−")}{Math.Abs(hours):0.#}h";
            int dayDiff = (there.Date - s.Time.Date).Days;
            sub.Text = dayDiff switch
            {
                > 0 => L.F("{0} · tomorrow", offset),
                < 0 => L.F("{0} · yesterday", offset),
                _ => offset,
            };
        }
    }
}

/// <summary>Laptop battery: charge, power source, time left.</summary>
public sealed class BatteryModule : Module
{
    readonly TextBlock _percent, _state, _left;
    readonly Meter _bar = new(10);

    public BatteryModule(ModuleConfig config) : base(config, "Battery")
    {
        var stat = W.Stat("", out _percent, out _state);
        var left = W.Row(L.T("Time left"), out _left);
        left.Margin = new Thickness(0, 4, 0, 0);
        View = MakeTile(Body(new Border(), stat, _bar, left.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var b = s.Battery;
        if (b == null)
            return;
        if (!b.HasBattery)
        {
            _percent.Text = "AC";
            _state.Text = L.T("no battery, running on mains power");
            _bar.Set(100, Theme.Good);
            _left.Text = "–";
            return;
        }

        _percent.Text = $"{b.Percent:0}%";
        _state.Text = b.Charging ? L.T("charging") : b.PluggedIn ? L.T("plugged in") : L.T("on battery");
        var color = b.Percent <= 10 && !b.PluggedIn ? Theme.Crit : b.Percent <= 25 && !b.PluggedIn ? Theme.Warn : Theme.Good;
        _percent.Foreground = b.Percent <= 25 && !b.PluggedIn ? color : Theme.Text;
        _bar.Set(b.Percent, color);
        _left.Text = b.Remaining is TimeSpan t && !b.PluggedIn ? W.Duration(t) : b.PluggedIn ? "–" : L.T("estimating…");
    }
}
