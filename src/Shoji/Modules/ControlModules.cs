using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Grid of touch buttons, each running a configured action (see <see cref="DeckActions"/>).</summary>
public sealed class DeckModule : Module
{
    sealed record Cell(DeckButton Button, Border Box, TextBlock Icon, TextBlock Label, TextBlock Sub);

    readonly IDashboardHost _host;
    readonly List<Cell> _cells = new();
    readonly bool _usesAudio;
    readonly IDisposable? _obs; // keeps OBS connected while this deck has OBS buttons
    int _obsQueued;

    public DeckModule(ModuleConfig config, IDashboardHost host, ObsConfig obs) : base(config, "Control deck")
    {
        _host = host;
        var buttons = config.Buttons ?? DeckDefaults.Buttons();
        _usesAudio = buttons.Any(b => b.Action?.ToLowerInvariant() is "mic" or "mute" or "volume" or "output");
        if (buttons.Any(ObsClient.IsObsButton))
        {
            _obs = ObsClient.Shared.Use(obs, ObsClient.MuteInputs(buttons));
            ObsClient.Shared.Changed += OnObsChanged;
        }

        int cols = config.OptInt("Columns", 0);
        if (cols <= 0)
            cols = buttons.Count <= 4 ? Math.Max(1, buttons.Count) : buttons.Count <= 8 ? 4 : buttons.Count <= 15 ? 5 : 6;
        var grid = new UniformGrid { Columns = cols };
        foreach (var b in buttons)
            grid.Children.Add(MakeCell(b));

        View = MakeTile(buttons.Count == 0
            ? W.Text(L.T("No buttons yet: Settings → Deck → Add button."), Theme.Small, Theme.Dim)
            : grid);
    }

    UIElement MakeCell(DeckButton b)
    {
        var icon = Icon.Make(b.Icon, 26);
        var label = W.Text(b.Label, Theme.Small);
        var sub = W.Text("", Theme.Small - 2, Theme.Dim);
        label.HorizontalAlignment = sub.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 4, 0, 0);

        var square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        var box = new Border
        {
            Background = Theme.Track,
            BorderBrush = Theme.TileBorder,
            BorderThickness = new Thickness(Theme.TileBorder == null ? 0 : 1),
            CornerRadius = new CornerRadius(square ? 0 : Math.Min(10, Theme.Current.Radius)),
            Margin = new Thickness(4),
            MinHeight = 64,
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, label, sub } },
        };
        var cell = new Cell(b, box, icon, label, sub);
        _cells.Add(cell);
        Tap.On(box, () =>
        {
            DeckActions.Run(b, _host);
            Refresh(cell);
        });
        return box;
    }

    public override void Update(Snapshot s)
    {
        foreach (var c in _cells)
            Refresh(c);
    }

    /// <summary>OBS state changed (on a socket thread): refresh just the OBS buttons, once per burst of events.</summary>
    void OnObsChanged()
    {
        if (Interlocked.Exchange(ref _obsQueued, 1) == 1)
            return;
        View.Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _obsQueued, 0);
            foreach (var c in _cells)
                if (ObsClient.IsObsButton(c.Button))
                    Refresh(c);
        });
    }

    protected override void OnDispose()
    {
        if (_obs == null)
            return;
        ObsClient.Shared.Changed -= OnObsChanged;
        _obs.Dispose();
    }

    /// <summary>Buttons that toggle something show its current state (muted mic turns red, etc.).</summary>
    void Refresh(Cell c)
    {
        var audio = _usesAudio ? AudioService.Shared : null;
        bool? active = null;
        bool danger = false;
        string? sub = null;
        switch (c.Button.Action?.ToLowerInvariant())
        {
            case "mic":
                active = audio?.MicMuted;
                danger = true;
                Icon.Set(c.Icon, active == true ? "micoff" : string.IsNullOrEmpty(c.Button.Icon) ? "mic" : c.Button.Icon);
                sub = active == true ? L.T("muted") : L.T("on");
                break;
            case "mute":
                active = audio?.Muted;
                danger = true;
                sub = active == true ? L.T("muted") : $"{audio?.Volume * 100:0}%";
                break;
            case "volume":
                sub = $"{audio?.Volume * 100:0}%";
                break;
            case "output":
                sub = Short(audio?.OutputName);
                break;
            case "scenes":
                active = _host.ScenesEnabled;
                sub = active == true ? L.T("on") : L.T("off");
                break;
            case "theme":
                sub = _host.ThemeName;
                break;
            case "page":
                sub = _host.PageName;
                break;
            case "obs":
                // Recording turns red with its running time, LIVE / the current scene / on light up; offline is dimmed.
                var obs = ObsClient.Shared.LookFor(c.Button.Target, c.Button.Icon);
                active = obs.Active;
                danger = obs.Danger;
                sub = obs.Sub;
                if (obs.Icon != null)
                    Icon.Set(c.Icon, obs.Icon);
                c.Box.Opacity = obs.Online ? 1 : 0.45;
                break;
        }

        c.Sub.Text = sub ?? "";
        c.Sub.Visibility = sub == null ? Visibility.Collapsed : Visibility.Visible;
        var fg = active == true ? Theme.Background : null;
        c.Box.Background = active == true ? danger ? Theme.Crit : Theme.Accent : Theme.Track;
        c.Icon.Foreground = c.Label.Foreground = fg ?? Theme.Text;
        c.Sub.Foreground = fg ?? Theme.Dim;
    }

    // "Speakers (Realtek(R) Audio)" → "Speakers"
    static string? Short(string? device) => device == null ? null : device.Split(" (")[0];
}

/// <summary>Master volume, microphone and per-app volume sliders.</summary>
public sealed class MixerModule : Module
{
    /// <summary>One slider row. The speaker icon + name on the left is the mute button (icon shows the state).</summary>
    sealed record Row(TouchSlider Slider, TextBlock Value, TextBlock Name, TextBlock Icon, string OnIcon, string OffIcon);

    readonly StackPanel _apps = new();
    readonly TextBlock _output;
    readonly Row _master, _mic;
    readonly Dictionary<string, Row> _appRows = new();
    string _appKey = "";

    public MixerModule(ModuleConfig config) : base(config, "Volume")
    {
        var audio = AudioService.Shared;
        _output = W.Text("", Theme.Small, Theme.Dim);
        Tap.On(_output, () => audio.SwitchOutput(null)); // tap the device name to switch output

        _master = MakeRow(L.T("Master"), v => audio.Volume = (float)v);
        _mic = MakeRow(L.T("Mic"), v => audio.MicVolume = (float)v, "mic", "micoff");
        // Tap the speaker icon / name to mute or unmute.
        var top = W.Stack(RowView(_master, audio.ToggleMute), RowView(_mic, audio.ToggleMic),
                          new Border { Height = 1, Background = Theme.Track, Margin = new Thickness(0, 6, 0, 6) });

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(new ScrollViewer { Content = _apps, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        View = MakeTile(body, _output);
    }

    static Row MakeRow(string name, Action<double> set, string onIcon = "volume", string offIcon = "mute")
    {
        var slider = new TouchSlider();
        slider.Changed += set;
        var value = W.Text("", Theme.Small, Theme.Dim);
        var label = W.Text(name, Theme.Small);
        label.VerticalAlignment = VerticalAlignment.Center;
        var icon = Icon.Make(onIcon, 15, Theme.Dim);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        return new Row(slider, value, label, icon, onIcon, offIcon);
    }

    static Grid RowView(Row row, Action toggleMute)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        // Icon + name form one mute button as tall as the slider row (easy to hit on a touch screen).
        var muteArea = new DockPanel { Background = Brushes.Transparent, MinHeight = 40 };
        row.Icon.Width = 26;
        DockPanel.SetDock(row.Icon, Dock.Left);
        muteArea.Children.Add(row.Icon);
        muteArea.Children.Add(row.Name);
        muteArea.ToolTip = L.T("Mute / unmute");
        Tap.On(muteArea, toggleMute);
        row.Value.HorizontalAlignment = HorizontalAlignment.Right;
        row.Value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(row.Slider, 1);
        Grid.SetColumn(row.Value, 2);
        g.Children.Add(muteArea);
        g.Children.Add(row.Slider);
        g.Children.Add(row.Value);
        return g;
    }

    public override void Update(Snapshot s)
    {
        var audio = AudioService.Shared;
        _output.Text = (audio.OutputName ?? L.T("No output")).Split(" (")[0] + "  ⇄";

        Show(_master, audio.Volume, audio.Muted);
        Show(_mic, audio.MicVolume, audio.MicMuted);

        var apps = audio.Apps();
        string key = string.Join(",", apps.Select(a => a.Id));
        if (key != _appKey)
        {
            // Apps came or went: rebuild the rows (rare).
            _appKey = key;
            _apps.Children.Clear();
            _appRows.Clear();
            foreach (var a in apps)
            {
                var id = a.Id;
                var row = MakeRow(L.T(a.Name), v => audio.SetAppVolume(id, (float)v)); // "System sounds" is translated
                var view = RowView(row, () => audio.SetAppMuted(id, !(audio.Apps().FirstOrDefault(x => x.Id == id)?.Muted ?? false)));
                _appRows[id] = row;
                _apps.Children.Add(view);
            }
            if (apps.Count == 0)
                _apps.Children.Add(W.Text(L.T("No app is playing sound."), Theme.Small, Theme.Dim));
        }
        foreach (var a in apps)
            if (_appRows.TryGetValue(a.Id, out var row))
                Show(row, a.Volume, a.Muted);
    }

    static void Show(Row row, float? volume, bool? muted)
    {
        row.Slider.Value = volume ?? 0;
        row.Slider.Fill = muted == true ? Theme.Dim : null;
        row.Slider.InvalidateVisual();
        row.Value.Text = volume == null ? "–" : muted == true ? L.T("mute") : $"{volume * 100:0}";
        Icon.Set(row.Icon, muted == true ? row.OffIcon : row.OnIcon);
        row.Icon.Foreground = muted == true ? Theme.Crit : Theme.Dim;
    }
}

/// <summary>Pomodoro timer: focus / break with touch controls.</summary>
public sealed class TimerModule : Module
{
    readonly FocusTimer _timer;
    readonly TextBlock _time, _phase, _playIcon;
    readonly Meter _progress = new(6);

    public TimerModule(ModuleConfig config, Collector collector) : base(config, "Focus timer")
    {
        int focus = config.OptInt("Focus", 25), pause = config.OptInt("Break", 5);
        _timer = collector.Service($"timer:{focus}:{pause}", () => new FocusTimer(focus, pause));

        _time = W.Number("25:00", 54);
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        _phase = W.Text("", Theme.Small, Theme.Dim);
        _phase.HorizontalAlignment = HorizontalAlignment.Center;

        _playIcon = Icon.Make("play", 20);
        var controls = W.Columns(
            Button(_playIcon, () => _timer.StartPause()),
            Button(Icon.Make("refresh", 20), () => _timer.Reset()),
            Button(Icon.Make("next", 20), () => _timer.Skip()));
        controls.Margin = new Thickness(0, 6, 0, 0);

        var body = new DockPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        DockPanel.SetDock(_progress, Dock.Bottom);
        body.Children.Add(controls);
        body.Children.Add(_progress);
        body.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, MaxHeight = 160,
            Child = W.Stack(_time, _phase),
        });
        View = MakeTile(body);
    }

    static Border Button(TextBlock icon, Action action)
    {
        var square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        return Tap.On(new Border
        {
            Child = icon, Height = 44, Margin = new Thickness(3), Background = Theme.Track,
            CornerRadius = new CornerRadius(square ? 0 : 8),
        }, action);
    }

    public override void Update(Snapshot s)
    {
        var left = _timer.Remaining;
        _time.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        _time.Foreground = _timer.OnBreak ? Theme.Good : Theme.Text;
        _phase.Text = (_timer.OnBreak ? L.T("break") : L.T("focus")) + (_timer.Running ? "" : L.T(" · paused")) +
                      (_timer.Completed > 0 ? L.F(" · {0} done", _timer.Completed) : "");
        Icon.Set(_playIcon, _timer.Running ? "pause" : "play");
        _progress.Set((1 - left.TotalSeconds / _timer.Length.TotalSeconds) * 100, _timer.OnBreak ? Theme.Good : Theme.Accent);
    }
}
