using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;


public sealed class ClockModule : Module
{
    static readonly System.Globalization.CultureInfo Japanese = new("ja-JP");

    readonly TextBlock _time, _seconds, _date, _uptime;
    readonly TextBlock? _jpDate, _next;
    readonly WeatherBadge? _weather;
    readonly AgendaService? _agenda;
    readonly bool _use24Hour;

    /// <param name="agenda">Calendars for the "Next: Standup in 12 min" line (null = no line).</param>
    public ClockModule(ModuleConfig config, bool use24Hour, AgendaService? agenda = null) : base(config, "Today")
    {
        _use24Hour = use24Hour;
        _agenda = agenda;
        _time = W.Number("", 64, weight: UI.Theme.Current.NumberWeight.ToAvalonia() == FontWeight.Bold ? FontWeight.SemiBold : FontWeight.Light);
        _seconds = W.Text("", 24, UI.Theme.Dim, FontWeight.Light);
        _seconds.FontFamily = UI.Theme.Display;
        _seconds.VerticalAlignment = VerticalAlignment.Bottom;
        _seconds.Margin = new Thickness(6, 0, 0, 12);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -10, 0, 0) };
        timeRow.Children.Add(_time);
        timeRow.Children.Add(_seconds);

        _date = W.Text("", UI.Theme.Medium + 2);
        bool large = config.OptBool("Large");
        if (!config.OptBool("HideWeather"))
            _weather = new WeatherBadge(large ? 1.15 : 1);

        // Weather sits to the right of the time; the time keeps its width and the weather trims first.
        Control head = timeRow;
        if (_weather != null && !large)
        {
            var dock = new DockPanel { LastChildFill = true };
            _weather.Margin = new Thickness(12, -6, 0, 0);
            _weather.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(timeRow, Dock.Left);
            dock.Children.Add(timeRow);
            dock.Children.Add(_weather);
            head = dock;
        }
        var top = new List<Control> { head, _date };
        if (UI.Theme.Current.Japanese)
        {
            // 2026年10月8日（木）
            _jpDate = W.Text("", UI.Theme.Medium, UI.Theme.Accent);
            _jpDate.FontFamily = UI.Theme.TitleFont;
            _jpDate.Margin = new Thickness(0, 2, 0, 0);
            top.Add(_jpDate.ShowFrom(Density.Normal));
        }
        if (_agenda != null)
        {
            // Shown only while there's something on or coming up; the tile's density rules don't apply to it.
            _next = W.Text("", UI.Theme.Small + 1, UI.Theme.Accent);
            _next.Margin = new Thickness(0, 4, 0, 0);
            _next.IsVisible = false;
            top.Add(_next);
        }

        var up = W.Row("PC uptime", out _uptime);
        up.Margin = new Thickness(0, 10, 0, 0);
        up.VerticalAlignment = VerticalAlignment.Bottom;

        if (large)
        {
            // Ambient mode: the time scales up to fill the tile, date, weather and uptime underneath.
            top.Remove(timeRow);
            timeRow.Margin = new Thickness(0);
            var bottom = W.Stack(top.ToArray());
            if (_weather != null)
            {
                _weather.Margin = new Thickness(0, 8, 0, 0);
                bottom.Children.Add(_weather);
            }
            bottom.Children.Add(up);
            foreach (Control e in bottom.Children)
                e.HorizontalAlignment = HorizontalAlignment.Center;
            // The lines underneath never take more than two fifths of the tile: on a short tile they shrink together,
            // so the time is still the big thing (it used to end up smaller than the date there).
            var under = new Viewbox { Child = bottom, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(under, 1);
            grid.Children.Add(new Viewbox { Child = timeRow, Stretch = Stretch.Uniform });
            grid.Children.Add(under);
            void Share()
            {
                double natural = bottom.Bounds.Height, room = grid.Bounds.Height;
                if (natural <= 0 || room <= 0)
                    return;
                double height = Math.Round(Math.Min(natural, room * 0.4));
                if (!grid.RowDefinitions[1].Height.IsAbsolute || Math.Abs(grid.RowDefinitions[1].Height.Value - height) >= 1)
                    grid.RowDefinitions[1].Height = new GridLength(height, GridUnitType.Pixel);
            }
            grid.SizeChanged += (_, _) => Share();
            bottom.SizeChanged += (_, _) => Share();
            View = MakeTile(grid);
            return;
        }

        // On a very small screen the time and date scale down together instead of the date being cut off.
        View = MakeTile(Body(up.ShowFrom(Density.Normal), new FitHeight(W.Stack(top.ToArray()))));
    }

    static DockPanel Bottom(Control fill, Control bottom)
    {
        var d = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        d.Children.Add(bottom);
        d.Children.Add(fill);
        return d;
    }

    public override void Update(Snapshot s)
    {
        _time.Text = s.Time.ToString(_use24Hour ? "HH:mm" : "h:mm");
        _seconds.Text = s.Time.ToString(_use24Hour ? "ss" : "ss tt");
        _date.Text = s.Time.ToString(L.T("dddd, d MMMM"), L.Culture);
        if (_jpDate != null)
            _jpDate.Text = s.Time.ToString("yyyy年M月d日（ddd）", Japanese);
        _uptime.Text = W.Duration(s.Uptime);
        _weather?.Update(s.Weather);
        if (_next != null)
        {
            string? line = CalendarFeeds.NextLine(_agenda!.State, s.Time, _use24Hour);
            _next.Text = line ?? "";
            _next.Foreground = line != null && (line.StartsWith("Now") || line.StartsWith(L.T("Now"))) ? UI.Theme.Accent : UI.Theme.Text;
            _next.IsVisible = line != null;
        }
    }
}

public sealed class DisksModule : Module
{
    readonly RowList _list = new(withBar: true);
    readonly TextBlock _io;

    public DisksModule(ModuleConfig config) : base(config, "Drives")
    {
        _io = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        View = MakeTile(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.FadeOverflow(), _io);
    }

    public override void Update(Snapshot s)
    {
        for (int i = 0; i < s.Disks.Count; i++)
        {
            var d = s.Disks[i];
            double pct = d.TotalGb > 0 ? d.UsedGb / d.TotalGb * 100 : 0;
            string label = string.IsNullOrEmpty(d.Label) ? d.Name : $"{d.Name}  {d.Label}";
            _list.Set(i, label, L.F("{0:0} GB free of {1:0}", d.TotalGb - d.UsedGb, d.TotalGb), pct);
        }
        _list.Trim(s.Disks.Count);

        _io.Text = s.DiskIo is { } io ? $"R {W.Bytes(io.ReadBytesPerSec)}/s · W {W.Bytes(io.WriteBytesPerSec)}/s" : "";
    }
}

/// <summary>Each drive's own verdict on its health (SMART), with wear, running time and temperature.</summary>
public sealed class DriveHealthModule : Module
{
    readonly DriveHealthService _service = ServiceStore.Get(DriveHealthService.Key, () => new DriveHealthService());
    readonly StackPanel _list = new();
    readonly List<(TextBlock Name, TextBlock State, TextBlock Details)> _rows = new();
    readonly TextBlock _summary;
    IReadOnlyList<DriveHealth>? _shown;

    public DriveHealthModule(ModuleConfig config) : base(config, "Drive health")
    {
        _summary = W.Text(L.T("Checking the drives…"), UI.Theme.Small, UI.Theme.Dim);
        View = MakeTile(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.FadeOverflow(), _summary);
    }

    public override void Update(Snapshot s)
    {
        var drives = _service.State;
        if (drives == null)
        {
            if (_service.Error != null)
                _summary.Text = L.T("Can't read the drives");
            return;
        }
        if (ReferenceEquals(drives, _shown))
            return; // a new list only arrives every ten minutes
        _shown = drives;

        while (_rows.Count < drives.Count)
        {
            var row = W.Row("", out var state, out var name);
            name.Foreground = UI.Theme.Text;
            var details = W.Text("", UI.Theme.Small, UI.Theme.Dim);
            // Wear, age, temperature and data written rarely fit one line: wrap instead of cutting the end off.
            details.TextTrimming = TextTrimming.None;
            details.TextWrapping = TextWrapping.Wrap;
            details.Margin = new Thickness(0, 0, 0, 7);
            _list.Children.Add(row);
            _list.Children.Add(details);
            _rows.Add((name, state, details));
        }
        while (_rows.Count > drives.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
            _list.Children.RemoveRange(_list.Children.Count - 2, 2);
        }
        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            var (name, state, details) = _rows[i];
            name.Text = d.Name;
            state.Text = DriveHealthText.State(d.State);
            state.Foreground = d.State switch { DriveState.Good => UI.Theme.Good, DriveState.Watch => UI.Theme.Warn, DriveState.Failing => UI.Theme.Crit, _ => UI.Theme.Dim };
            details.Text = d.Problem == null ? DriveHealthText.Details(d) : d.Problem + " · " + DriveHealthText.Details(d);
        }
        _summary.Text = DriveHealthText.Summary(drives);
        _summary.Foreground = drives.Any(d => d.State == DriveState.Failing) ? UI.Theme.Crit : drives.Any(d => d.State == DriveState.Watch) ? UI.Theme.Warn : UI.Theme.Dim;
    }
}

/// <summary>How much Claude Code and Codex were used on this PC: tokens in the last 5 and 24 hours, per model, per hour.</summary>
public sealed class AiUsageModule : Module
{
    readonly AiUsageService _service = ServiceStore.Get(AiUsageService.Key, () => new AiUsageService());
    readonly TextBlock _five, _day, _note;
    readonly RowList _limits = new(withBar: true), _rows = new();
    readonly HourBars _bars = new() { MinHeight = 22, Margin = new Thickness(0, 6, 0, 0) };
    AiUsage? _shown;
    DateTime _shownAt;

    public AiUsageModule(ModuleConfig config) : base(config, "AI usage")
    {
        _note = W.Text(L.T("Reading the logs…"), UI.Theme.Small, UI.Theme.Dim);
        var stats = W.Columns(W.Stat("tokens, last 5 hours", out _five), W.Stat("last 24 hours", out _day));
        _five.Text = _day.Text = "–";
        _limits.Margin = new Thickness(0, 5, 0, 0);
        View = MakeTile(Body(_bars.ShowFrom(Density.Normal), stats, _limits, _rows.ShowFrom(Density.Normal)), _note);
    }

    public override void Update(Snapshot s)
    {
        var u = _service.State;
        // New numbers arrive once a minute; the "resets in" texts are refreshed on the same beat.
        if (u == null || ReferenceEquals(u, _shown) && (s.Time - _shownAt).Duration() < TimeSpan.FromSeconds(30))
            return;
        _shown = u;
        _shownAt = s.Time;
        if (!u.Found)
        {
            _note.Text = L.T("No Claude Code or Codex on this PC");
            return;
        }
        _five.Text = AiUsageText.Tokens(u.Tokens5h);
        _day.Text = AiUsageText.Tokens(u.Tokens24h);
        _note.Text = "5 h · 24 h";

        var now = s.Time.ToUniversalTime();
        for (int i = 0; i < u.Limits.Count; i++)
        {
            var l = u.Limits[i];
            string resets = AiUsageText.Resets(l.ResetsAt, now);
            _limits.Set(i, l.Label, resets.Length > 0 ? $"{l.UsedPercent:0}% · {resets}" : $"{l.UsedPercent:0}%", l.UsedPercent);
        }
        _limits.Trim(u.Limits.Count);

        int row = 0;
        _rows.Set(row++, L.T("Answers"), $"{u.Messages5h.ToString(L.Culture)} · {u.Messages24h.ToString(L.Culture)}");
        _rows.Set(row++, L.T("Written"), $"{AiUsageText.Tokens(u.Output5h)} · {AiUsageText.Tokens(u.Output24h)}");
        foreach (var m in u.Models.Take(4))
            _rows.Set(row++, m.Model, $"{AiUsageText.Tokens(m.Tokens5h)} · {AiUsageText.Tokens(m.Tokens24h)}");
        _rows.Trim(row);

        _bars.Data = u.Hourly;
        _bars.InvalidateVisual();
    }

    /// <summary>One bar per hour of the last 24, the running hour on the right in the accent colour.</summary>
    sealed class HourBars : DrawnControl
    {
        public IReadOnlyList<double> Data = Array.Empty<double>();

        protected override void Draw(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            int n = Data.Count;
            if (w <= 0 || h <= 0 || n == 0)
                return;
            double max = Math.Max(1, Data.Max()), slot = w / n, bar = Math.Max(1, slot - Math.Min(3, slot * 0.25));
            for (int i = 0; i < n; i++)
            {
                double bh = Data[i] <= 0 ? 1.5 : Math.Max(2, Data[i] / max * h);
                dc.DrawRectangle(Data[i] <= 0 ? UI.Theme.Track : i == n - 1 ? UI.Theme.Accent : UI.Theme.Dim, null, new Rect(i * slot, h - bh, bar, bh));
            }
        }
    }
}

public sealed class ProcessesModule : Module
{
    readonly RowList _list = new();

    public ProcessesModule(ModuleConfig config) : base(config, "Top processes")
    {
        View = MakeTile(_list, W.Text("CPU · RAM", UI.Theme.Small, UI.Theme.Dim));
    }

    public override void Update(Snapshot s)
    {
        for (int i = 0; i < s.TopProcesses.Count; i++)
        {
            var p = s.TopProcesses[i];
            string mem = p.MemoryMb >= 1024 ? $"{p.MemoryMb / 1024:0.0} GB" : $"{p.MemoryMb:0} MB";
            _list.Set(i, p.Name, $"{p.CpuPercent,5:0.0}%  ·  {mem,7}", valueColor: p.CpuPercent >= 25 ? UI.Theme.Warn : UI.Theme.Text);
        }
        _list.Trim(s.TopProcesses.Count);
    }
}

public sealed class ActivityModule : Module
{
    readonly TextBlock _app, _title, _focused, _today;
    readonly RowList _list = new(withBar: true);

    public ActivityModule(ModuleConfig config) : base(config, "Activity")
    {
        _app = W.Text("", UI.Theme.Medium + 3, weight: FontWeight.SemiBold);
        _title = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var focus = W.Columns(W.Row("Focused", out _focused), W.Row("Active today", out _today));
        focus.Margin = new Thickness(0, 4, 0, 8);
        View = MakeTile(Body(_list.ShowFrom(Density.Normal), _app, _title.ShowFrom(Density.Normal), focus));
    }

    public override void Update(Snapshot s)
    {
        var a = s.Activity;
        if (a == null)
            return;

        _app.Text = a.ProcessName;
        _title.Text = a.WindowTitle;
        _focused.Text = a.IdleFor.TotalMinutes >= 2 ? L.F("idle {0}", W.Duration(a.IdleFor)) : W.Duration(a.FocusedFor);
        _today.Text = W.Duration(a.TodayActive);

        double max = a.TodayTopApps.Count > 0 ? a.TodayTopApps[0].Duration.TotalSeconds : 1;
        for (int i = 0; i < a.TodayTopApps.Count; i++)
        {
            var u = a.TodayTopApps[i];
            _list.Set(i, u.ProcessName, W.Duration(u.Duration), u.Duration.TotalSeconds / Math.Max(max, 1) * 100);
        }
        _list.Trim(a.TodayTopApps.Count);
    }
}

public sealed class MediaModule : Module
{
    readonly LinuxMedia _media;
    readonly Image _art = new() { Width = 72, Height = 72, Stretch = Stretch.UniformToFill };
    readonly TextBlock _title, _artist, _app, _time, _playIcon;
    readonly Meter _progress = new(4);
    readonly TextBlock _note;
    byte[]? _shownThumb;

    public MediaModule(ModuleConfig config, LinuxMedia media) : base(config, "Now playing")
    {
        _media = media;
        _title = W.Text(L.T("Nothing playing"), UI.Theme.Medium + 1, weight: FontWeight.SemiBold);
        _artist = W.Text("", UI.Theme.Small + 1, UI.Theme.Dim);
        _app = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        _time = W.Text("", UI.Theme.Small, UI.Theme.Dim, mono: true);

        var artFrame = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(8), Background = UI.Theme.Track,
            Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true,
        };
        _art.Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), 8, 8);
        // A music note sits under the cover, so a song without artwork doesn't show an empty grey square.
        var note = W.Text("music_note", 30, UI.Theme.Dim);
        note.FontFamily = UI.Theme.Icons;
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        _note = note;
        var cover = new Grid();
        cover.Children.Add(note);
        cover.Children.Add(_art);
        artFrame.Child = cover;
        var text = W.Stack(_title, _artist, _time.ShowFrom(Density.Normal));
        var top = new DockPanel();
        DockPanel.SetDock(artFrame, Dock.Left);
        top.Children.Add(artFrame.ShowFrom(Density.Normal));
        top.Children.Add(text);

        bool large = config.OptBool("Large");
        if (large)
        {
            // Big cover for the Media page, sized to the tile (see FitLarge) so text stays readable on short screens.
            artFrame.Width = artFrame.Height = _art.Width = _art.Height = 200;
            artFrame.CornerRadius = new CornerRadius(14);
            _art.Clip = new RectangleGeometry(new Rect(0, 0, 200, 200), 14, 14);
            artFrame.Margin = new Thickness(0, 0, 24, 0);
            _title.FontSize = UI.Theme.Medium + 13;
            _title.FontFamily = UI.Theme.Display;
            _title.TextWrapping = TextWrapping.Wrap;
            _title.MaxWidth = 520;
            _artist.FontSize = UI.Theme.Medium + 3;
            _time.FontSize = UI.Theme.Medium;
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        // Big touch-friendly buttons – many small screens are touchscreens.
        _playIcon = W.Text("play_arrow", 22); // Segoe Fluent/MDL2 "Play"
        var controls = W.Columns(
            Button("skip_previous", () => _media.PreviousAsync()),
            Button(_playIcon, () => _media.PlayPauseAsync()),
            Button("skip_next", () => _media.NextAsync()));
        controls.Margin = new Thickness(0, 6, 0, 0);
        controls.VerticalAlignment = VerticalAlignment.Bottom;

        if (large)
        {
            artFrame.ClearValue(Responsive.ShowFromProperty); // FitLarge decides when the cover fits
            artFrame.VerticalAlignment = VerticalAlignment.Center;
            var scaled = new Border { Child = top, ClipToBounds = true };
            scaled.SizeChanged += (_, _) => FitLarge(scaled.Bounds.Width, scaled.Bounds.Height, artFrame);
            var body = new DockPanel();
            DockPanel.SetDock(controls, Dock.Bottom);
            DockPanel.SetDock(_progress, Dock.Bottom);
            body.Children.Add(controls);
            body.Children.Add(_progress);
            body.Children.Add(scaled);
            View = MakeTile(body, _app);
            return;
        }
        // A tall tile (a wide one on a page with few rows) gives the cover the height the list would leave empty,
        // instead of a small square over a blank band.
        var normal = Body(controls, top, _progress);
        normal.SizeChanged += (_, _) =>
        {
            double room = normal.Bounds.Height - _progress.Bounds.Height - controls.DesiredSize.Height - 16;
            double size = Math.Round(Math.Clamp(room, 72, 150));
            if (Math.Abs(size - artFrame.Width) < 1)
                return;
            artFrame.Width = artFrame.Height = _art.Width = _art.Height = size;
            _art.Clip = new RectangleGeometry(new Rect(0, 0, size, size), 8, 8);
            _note.FontSize = Math.Round(size * 0.42);
            text.VerticalAlignment = size > 80 ? VerticalAlignment.Center : VerticalAlignment.Top;
        };
        View = MakeTile(normal, _app);
    }

    /// <summary>
    /// The big layout: the cover takes the tile's height (up to 220 px, hidden below 56) and the text scales with it,
    /// never below a readable size. It used to shrink the whole block as a picture, which made text tiny on short screens.
    /// </summary>
    void FitLarge(double w, double h, Border artFrame)
    {
        if (w <= 0 || h <= 0)
            return;
        double art = Math.Floor(Math.Min(Math.Min(h - 4, w * 0.42), 220));
        bool showArt = art >= 56;
        artFrame.IsVisible = showArt;
        if (showArt && Math.Abs(artFrame.Width - art) > 0.5)
        {
            artFrame.Width = artFrame.Height = _art.Width = _art.Height = art;
            double r = Math.Clamp(art * 0.07, 6, 14);
            artFrame.CornerRadius = new CornerRadius(r);
            _art.Clip = new RectangleGeometry(new Rect(0, 0, art, art), r, r);
            artFrame.Margin = new Thickness(0, 0, Math.Clamp(art * 0.12, 12, 24), 0);
            _note.FontSize = art * 0.42;
        }
        double k = Math.Clamp(h / 200, 0.6, 1);
        _title.FontSize = Math.Max(16, (UI.Theme.Medium + 13) * k);
        _artist.FontSize = Math.Max(13, (UI.Theme.Medium + 3) * k);
        _time.FontSize = Math.Max(11, UI.Theme.Medium * k);
        _title.MaxWidth = Math.Max(120, w - (showArt ? art + artFrame.Margin.Right : 0));
        _title.MaxHeight = _title.FontSize * 2.7; // two lines at most
    }

    static Border Button(string glyph, Func<System.Threading.Tasks.Task> action) => Button(W.Text(glyph, 22), action);

    static Border Button(TextBlock icon, Func<System.Threading.Tasks.Task> action)
    {
        icon.FontFamily = UI.Theme.Icons;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border
        {
            Child = icon, Height = 44, Margin = new Thickness(3), CornerRadius = new CornerRadius(8),
            Background = UI.Theme.Track,
        };
        // Same tap as every other button: press feedback, and only a press that started here counts (not a swipe).
        return Tap.On(b, () => _ = action());
    }

    public override void Update(Snapshot s)
    {
        var m = s.Media;
        if (m == null || string.IsNullOrEmpty(m.Title))
        {
            _title.Text = L.T("Nothing playing");
            _artist.Text = _app.Text = _time.Text = "";
            _art.Source = null;
            _shownThumb = null;
            _note.IsVisible = true;
            _progress.Set(0);
            _playIcon.Text = "play_arrow";
            return;
        }

        _title.Text = m.Title;
        _artist.Text = m.Artist;
        _app.Text = m.App;
        _playIcon.Text = m.IsPlaying ? "pause" : "play_arrow"; // Pause : Play

        if (m.Duration is TimeSpan d && m.Position is TimeSpan p && d > TimeSpan.Zero)
        {
            _time.Text = $"{p:m\\:ss} / {d:m\\:ss}";
            _progress.Set(p.TotalSeconds / d.TotalSeconds * 100);
        }
        else
        {
            _time.Text = L.T(m.IsPlaying ? "playing" : "paused");
            _progress.Set(0);
        }

        if (!ReferenceEquals(m.Thumbnail, _shownThumb))
        {
            _shownThumb = m.Thumbnail;
            _art.Source = m.Thumbnail == null ? null : LoadImage(m.Thumbnail);
            _note.IsVisible = _art.Source == null;
        }
    }

    static Bitmap? LoadImage(byte[] bytes)
    {
        try
        {
            return Bitmap.DecodeToWidth(new System.IO.MemoryStream(bytes), 144);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Any LibreHardwareMonitor sensors you pick by id in config.json (fans, pump, drive temps, voltages…).</summary>
public sealed class SensorsModule : Module
{
    readonly RowList _list = new();
    readonly List<SensorRef> _refs;
    // Up to four sensors are shown as big numbers, two a row: a short list would leave most of the tile empty.
    readonly List<(TextBlock Value, TextBlock Caption)> _stats = new();

    public SensorsModule(ModuleConfig config) : base(config, "Sensors")
    {
        _refs = config.Sensors ?? new List<SensorRef>();
        if (_refs.Count == 0)
        {
            View = MakeTile(W.Text(L.T("Tray icon → Export sensor list, then add sensor ids to config.json"), UI.Theme.Small, UI.Theme.Dim));
            return;
        }
        if (_refs.Count > 4)
        {
            View = MakeTile(_list);
            return;
        }
        var grid = new UniformGrid { Columns = _refs.Count == 1 ? 1 : 2, VerticalAlignment = VerticalAlignment.Top };
        foreach (var r in _refs)
        {
            var stat = W.Stat("", out var value, out var caption, UI.Theme.Big * 0.8);
            caption.Text = r.Label ?? r.Id;
            stat.Margin = new Thickness(0, 2, 8, 8);
            grid.Children.Add(stat);
            _stats.Add((value, caption));
        }
        View = MakeTile(grid);
    }

    public override void Update(Snapshot s)
    {
        for (int i = 0; i < _stats.Count; i++)
        {
            var r = _refs[i];
            var (value, caption) = _stats[i];
            bool found = s.Sensors.TryGetValue(r.Id, out var v);
            // The number big, its unit down in the caption ("CPU fan · rpm"): wide display fonts would cut "1240 rpm" off.
            string text = found ? FormatSensor(v!) : "n/a";
            int space = text.LastIndexOf(' ');
            string name = r.Label ?? (found ? v!.Name : r.Id);
            caption.Text = space > 0 ? $"{name} · {text[(space + 1)..]}" : name;
            value.Text = space > 0 ? text[..space] : text;
            value.Foreground = !found ? UI.Theme.Dim : v!.Type == "Temperature" ? UI.Theme.ForTemperature(v.Value) : UI.Theme.Text;
        }
        if (_stats.Count > 0)
            return;
        for (int i = 0; i < _refs.Count; i++)
        {
            var r = _refs[i];
            if (s.Sensors.TryGetValue(r.Id, out var v))
            {
                _list.Set(i, r.Label ?? v.Name, FormatSensor(v),
                    valueColor: v.Type == "Temperature" ? UI.Theme.ForTemperature(v.Value) : null);
            }
            else
            {
                _list.Set(i, r.Label ?? r.Id, "n/a", valueColor: UI.Theme.Dim);
            }
        }
    }

    static string FormatSensor(SensorValue v)
    {
        if (v.Value is not float x)
            return "–";
        return v.Type switch
        {
            "Temperature" => W.Temp(x, unit: true),
            "Fan" => $"{x:0} rpm",
            "Load" or "Control" or "Level" => $"{x:0}%",
            "Clock" => $"{x:0} MHz",
            "Voltage" => $"{x:0.000} V",
            "Current" => $"{x:0.00} A",
            "Power" => $"{x:0.0} W",
            "Data" => $"{x:0.0} GB",
            "SmallData" => $"{x:0} MB",
            "Throughput" => W.Rate(x),
            "Flow" => $"{x:0} L/h",
            _ => x.ToString("0.##"),
        };
    }
}
