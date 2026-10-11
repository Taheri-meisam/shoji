using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Stack of label/value rows (optionally with a bar) that grows and shrinks without rebuilding every tick.</summary>
sealed class RowList : StackPanel
{
    readonly bool _withBar;
    readonly List<(TextBlock label, TextBlock value, Meter? bar)> _rows = new();

    public RowList(bool withBar = false) => _withBar = withBar;

    public void Set(int index, string label, string value, double? percent = null, Brush? valueColor = null, Brush? barColor = null)
    {
        while (_rows.Count <= index)
        {
            var row = W.Row("", out var v);
            var l = (TextBlock)row.Children[0];
            l.Foreground = Theme.Text;
            Meter? bar = _withBar ? new Meter(5) { Margin = new Thickness(0, 1, 0, 6) } : null;
            Children.Add(row);
            if (bar != null)
                Children.Add(bar);
            _rows.Add((l, v, bar));
        }

        var (lb, vb, mb) = _rows[index];
        lb.Text = label;
        vb.Text = value;
        vb.Foreground = valueColor ?? Theme.Text;
        mb?.Set(percent, barColor ?? Theme.ForPercent(percent));
    }

    /// <summary>Removes rows beyond <paramref name="count"/>.</summary>
    public void Trim(int count)
    {
        int perRow = _withBar ? 2 : 1;
        while (_rows.Count > count)
        {
            _rows.RemoveAt(_rows.Count - 1);
            Children.RemoveRange(Children.Count - perRow, perRow);
        }
    }
}

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
        _time = W.Number("", 64, weight: Theme.Current.NumberWeight == FontWeights.Bold ? FontWeights.SemiBold : FontWeights.Light);
        _seconds = W.Text("", 24, Theme.Dim, FontWeights.Light);
        _seconds.FontFamily = Theme.Display;
        _seconds.VerticalAlignment = VerticalAlignment.Bottom;
        _seconds.Margin = new Thickness(6, 0, 0, 12);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -10, 0, 0) };
        timeRow.Children.Add(_time);
        timeRow.Children.Add(_seconds);

        _date = W.Text("", Theme.Medium + 2);
        bool large = config.OptBool("Large");
        if (!config.OptBool("HideWeather"))
            _weather = new WeatherBadge(large ? 1.15 : 1);

        // Weather sits to the right of the time; the time keeps its width and the weather trims first.
        UIElement head = timeRow;
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
        var top = new List<UIElement> { head, _date };
        if (Theme.Current.Japanese)
        {
            // 2026年10月8日（木）
            _jpDate = W.Text("", Theme.Medium, Theme.Accent);
            _jpDate.FontFamily = Theme.TitleFont;
            _jpDate.Margin = new Thickness(0, 2, 0, 0);
            top.Add(_jpDate.ShowFrom(Density.Normal));
        }
        if (_agenda != null)
        {
            // Shown only while there's something on or coming up; the tile's density rules don't apply to it.
            _next = W.Text("", Theme.Small + 1, Theme.Accent);
            _next.Margin = new Thickness(0, 4, 0, 0);
            _next.Visibility = Visibility.Collapsed;
            top.Add(_next);
        }

        var up = W.Row(L.T("PC uptime"), out _uptime);
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
            foreach (FrameworkElement e in bottom.Children)
                e.HorizontalAlignment = HorizontalAlignment.Center;
            View = MakeTile(Bottom(new Viewbox { Child = timeRow, Stretch = Stretch.Uniform }, bottom));
            return;
        }

        View = MakeTile(Body(up.ShowFrom(Density.Normal), top.ToArray()));
    }

    static DockPanel Bottom(UIElement fill, UIElement bottom)
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
            _next.Foreground = line != null && (line.StartsWith("Now") || line.StartsWith(L.T("Now"))) ? Theme.Accent : Theme.Text;
            _next.Visibility = line == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

public sealed class DisksModule : Module
{
    readonly RowList _list = new(withBar: true);
    readonly TextBlock _io;

    public DisksModule(ModuleConfig config) : base(config, "Drives")
    {
        _io = W.Text("", Theme.Small, Theme.Dim);
        View = MakeTile(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, _io);
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

public sealed class ProcessesModule : Module
{
    readonly RowList _list = new();

    public ProcessesModule(ModuleConfig config) : base(config, "Top processes")
    {
        View = MakeTile(_list, W.Text("CPU · RAM", Theme.Small, Theme.Dim));
    }

    public override void Update(Snapshot s)
    {
        for (int i = 0; i < s.TopProcesses.Count; i++)
        {
            var p = s.TopProcesses[i];
            string mem = p.MemoryMb >= 1024 ? $"{p.MemoryMb / 1024:0.0} GB" : $"{p.MemoryMb:0} MB";
            _list.Set(i, p.Name, $"{p.CpuPercent,5:0.0}%  ·  {mem,7}", valueColor: p.CpuPercent >= 25 ? Theme.Warn : Theme.Text);
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
        _app = W.Text("", Theme.Medium + 3, weight: FontWeights.SemiBold);
        _title = W.Text("", Theme.Small, Theme.Dim);
        var focus = W.Columns(W.Row(L.T("Focused"), out _focused), W.Row(L.T("Active today"), out _today));
        focus.Margin = new Thickness(0, 4, 0, 8);
        View = MakeTile(Body(_list.ShowFrom(Density.Normal), _app, _title.ShowFrom(Density.Normal), focus));
    }

    public override void Update(Snapshot s)
    {
        var a = s.Activity;
        if (a == null)
            return;

        _app.Text = L.T(a.ProcessName); // "Desktop" / "Unknown" are translated
        _title.Text = a.WindowTitle;
        _focused.Text = a.IdleFor.TotalMinutes >= 2 ? L.F("idle {0}", W.Duration(a.IdleFor)) : W.Duration(a.FocusedFor);
        _today.Text = W.Duration(a.TodayActive);

        double max = a.TodayTopApps.Count > 0 ? a.TodayTopApps[0].Duration.TotalSeconds : 1;
        for (int i = 0; i < a.TodayTopApps.Count; i++)
        {
            var u = a.TodayTopApps[i];
            _list.Set(i, L.T(u.ProcessName), W.Duration(u.Duration), u.Duration.TotalSeconds / Math.Max(max, 1) * 100);
        }
        _list.Trim(a.TodayTopApps.Count);
    }
}

public sealed class MediaModule : Module
{
    readonly MediaService _media;
    readonly Image _art = new() { Width = 72, Height = 72, Stretch = Stretch.UniformToFill };
    readonly TextBlock _title, _artist, _app, _time, _playIcon;
    readonly Meter _progress = new(4);
    byte[]? _shownThumb;

    public MediaModule(ModuleConfig config, MediaService media) : base(config, "Now playing")
    {
        _media = media;
        _title = W.Text(L.T("Nothing playing"), Theme.Medium + 1, weight: FontWeights.SemiBold);
        _artist = W.Text("", Theme.Small + 1, Theme.Dim);
        _app = W.Text("", Theme.Small, Theme.Dim);
        _time = W.Text("", Theme.Small, Theme.Dim, mono: true);

        var artFrame = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(8), Background = Theme.Track,
            Child = _art, Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true,
        };
        _art.Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), 8, 8);
        var text = W.Stack(_title, _artist, _time.ShowFrom(Density.Normal));
        var top = new DockPanel();
        DockPanel.SetDock(artFrame, Dock.Left);
        top.Children.Add(artFrame.ShowFrom(Density.Normal));
        top.Children.Add(text);

        bool large = config.OptBool("Large");
        if (large)
        {
            // Big cover for the Media page; the whole block scales down if the tile is smaller.
            artFrame.Width = artFrame.Height = _art.Width = _art.Height = 200;
            artFrame.CornerRadius = new CornerRadius(14);
            _art.Clip = new RectangleGeometry(new Rect(0, 0, 200, 200), 14, 14);
            artFrame.Margin = new Thickness(0, 0, 24, 0);
            _title.FontSize = Theme.Medium + 13;
            _title.FontFamily = Theme.Display;
            _title.TextWrapping = TextWrapping.Wrap;
            _title.MaxWidth = 520;
            _artist.FontSize = Theme.Medium + 3;
            _time.FontSize = Theme.Medium;
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        // Big touch-friendly buttons – many small screens are touchscreens.
        _playIcon = W.Text("", 22); // Segoe Fluent/MDL2 "Play"
        var controls = W.Columns(
            Button("", () => _media.PreviousAsync()),
            Button(_playIcon, () => _media.PlayPauseAsync()),
            Button("", () => _media.NextAsync()));
        controls.Margin = new Thickness(0, 6, 0, 0);
        controls.VerticalAlignment = VerticalAlignment.Bottom;

        if (large)
        {
            var scaled = new Viewbox { Child = top, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            var body = new DockPanel();
            DockPanel.SetDock(controls, Dock.Bottom);
            DockPanel.SetDock(_progress, Dock.Bottom);
            body.Children.Add(controls);
            body.Children.Add(_progress);
            body.Children.Add(scaled);
            View = MakeTile(body, _app);
            return;
        }
        View = MakeTile(Body(controls, top, _progress), _app);
    }

    static Border Button(string glyph, Func<System.Threading.Tasks.Task> action) => Button(W.Text(glyph, 22), action);

    static Border Button(TextBlock icon, Func<System.Threading.Tasks.Task> action)
    {
        icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border
        {
            Child = icon, Height = 44, Margin = new Thickness(3), CornerRadius = new CornerRadius(8),
            Background = Theme.Track,
        };
        // Same tap as every other button: press feedback, and only a press that started here counts (not a swipe).
        return Tap.On(b, async () => await action());
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
            _progress.Set(0);
            _playIcon.Text = "";
            return;
        }

        _title.Text = m.Title;
        _artist.Text = m.Artist;
        _app.Text = m.App;
        _playIcon.Text = m.IsPlaying ? "" : ""; // Pause : Play

        if (m.Duration is TimeSpan d && m.Position is TimeSpan p && d > TimeSpan.Zero)
        {
            _time.Text = $"{p:m\\:ss} / {d:m\\:ss}";
            _progress.Set(p.TotalSeconds / d.TotalSeconds * 100);
        }
        else
        {
            _time.Text = m.IsPlaying ? L.T("playing") : L.T("paused");
            _progress.Set(0);
        }

        if (!ReferenceEquals(m.Thumbnail, _shownThumb))
        {
            _shownThumb = m.Thumbnail;
            _art.Source = m.Thumbnail == null ? null : LoadImage(m.Thumbnail);
        }
    }

    static BitmapImage? LoadImage(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 144;
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
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

    public SensorsModule(ModuleConfig config) : base(config, "Sensors")
    {
        _refs = config.Sensors ?? new List<SensorRef>();
        View = MakeTile(_refs.Count == 0
            ? W.Text(L.T("Tray icon → Export sensor list, then add sensor ids to config.json"), Theme.Small, Theme.Dim)
            : _list);
    }

    public override void Update(Snapshot s)
    {
        for (int i = 0; i < _refs.Count; i++)
        {
            var r = _refs[i];
            if (s.Sensors.TryGetValue(r.Id, out var v))
            {
                _list.Set(i, r.Label ?? v.Name, FormatSensor(v),
                    valueColor: v.Type == "Temperature" ? Theme.ForTemperature(v.Value) : null);
            }
            else
            {
                _list.Set(i, r.Label ?? r.Id, L.T("n/a"), valueColor: Theme.Dim);
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
