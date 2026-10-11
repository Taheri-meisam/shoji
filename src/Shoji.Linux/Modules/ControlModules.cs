using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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
            ? W.Text(L.T("No buttons yet: Settings → Deck → Add button."), UI.Theme.Small, UI.Theme.Dim)
            : grid);
    }

    Control MakeCell(DeckButton b)
    {
        var icon = Icon.Make(b.Icon, 26);
        var label = W.Text(b.Label, UI.Theme.Small);
        var sub = W.Text("", UI.Theme.Small - 2, UI.Theme.Dim);
        label.HorizontalAlignment = sub.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 4, 0, 0);

        var square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        var box = new Border
        {
            Background = UI.Theme.Track,
            BorderBrush = UI.Theme.TileBorder,
            BorderThickness = new Thickness(UI.Theme.TileBorder == null ? 0 : 1),
            CornerRadius = new CornerRadius(square ? 0 : Math.Min(10, UI.Theme.Current.Radius)),
            Margin = new Thickness(4),
            MinHeight = 0,
            ClipToBounds = true, // short tiles: cells shrink and clip instead of overlapping (WPF clipped by itself)
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
        // Reading the audio state keeps wpctl polling; a deck on a hidden page doesn't need it (it refreshes when shown).
        if (_usesAudio && !View.IsEffectivelyVisible)
            return;
        foreach (var c in _cells)
            Refresh(c);
    }

    /// <summary>OBS state changed (on a socket thread): refresh just the OBS buttons, once per burst of events.</summary>
    void OnObsChanged()
    {
        if (Interlocked.Exchange(ref _obsQueued, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
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
        var audio = _usesAudio ? LinuxAudio.Shared : null;
        bool? active = null;
        bool danger = false;
        string? sub = null;
        switch (c.Button.Action?.ToLowerInvariant())
        {
            case "mic":
                active = audio?.MicMuted;
                danger = true;
                Icon.Set(c.Icon, active == true ? "micoff" : string.IsNullOrEmpty(c.Button.Icon) ? "mic" : c.Button.Icon);
                sub = L.T(active == true ? "muted" : "on");
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
                sub = L.T(active == true ? "on" : "off");
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
        c.Sub.IsVisible = !(sub == null);
        var fg = active == true ? UI.Theme.Background : null;
        c.Box.Background = active == true ? danger ? UI.Theme.Crit : UI.Theme.Accent : UI.Theme.Track;
        c.Icon.Foreground = c.Label.Foreground = fg ?? UI.Theme.Text;
        c.Sub.Foreground = fg ?? UI.Theme.Dim;
    }

    // "Speakers (Realtek(R) Audio)" → "Speakers"
    static string? Short(string? device) => device == null ? null : device.Split(" (")[0];
}

/// <summary>Master volume and microphone, a slider per app playing sound (PipeWire streams), and the audio outputs to switch between.</summary>
public sealed class MixerModule : Module
{
    /// <summary>An app's row: tap its icon / name to mute it.</summary>
    sealed record AppRow(TouchSlider Slider, TextBlock Value, TextBlock Icon);

    readonly TouchSlider _master = new();
    readonly TextBlock _value, _mic, _output, _icon;
    readonly StackPanel _outputs = new(), _apps = new();
    readonly Dictionary<string, AppRow> _appRows = new();
    string _outputKey = "", _appKey = "-"; // "-": no list built yet (an empty list still shows its "nothing playing" line)

    public MixerModule(ModuleConfig config) : base(config, "Volume")
    {
        var audio = LinuxAudio.Shared;
        _value = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        // Dragging fires many changes; apply the latest one at most ~8 times a second (each is a wpctl call).
        double? pending = null;
        var apply = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        apply.Tick += (_, _) =>
        {
            apply.Stop();
            if (pending is double v)
                audio.SetVolume(v);
            pending = null;
        };
        _master.Changed += v =>
        {
            pending = v;
            if (!apply.IsEnabled)
                apply.Start();
        };
        var name = W.Text(L.T("Master"), UI.Theme.Small);
        name.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*,44"), Margin = new Thickness(0, 1) };
        _icon = Icon.Make("volume", 15, UI.Theme.Dim);
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _icon.Width = 26;
        // Icon and name together are the mute button: a finger-sized target (at least 40 px tall), not just the word.
        var muteButton = Tap.On(new Border
        {
            Background = Brushes.Transparent, MinHeight = 40,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, name } },
        }, audio.ToggleMute);
        Grid.SetColumn(_master, 1);
        Grid.SetColumn(_value, 2);
        row.Children.Add(muteButton);
        row.Children.Add(_master);
        row.Children.Add(_value);
        _mic = W.Text("", UI.Theme.Small);
        _mic.VerticalAlignment = VerticalAlignment.Center;
        var micIcon = Icon.Make("mic", 15, UI.Theme.Dim);
        micIcon.VerticalAlignment = VerticalAlignment.Center;
        var micRow = Tap.On(new StackPanel { Orientation = Orientation.Horizontal, MinHeight = 40, Margin = new Thickness(0, 2, 0, 0), Background = Brushes.Transparent, Children = { micIcon, _mic } }, audio.ToggleMic);
        _mic.Margin = new Thickness(8, 0, 0, 0);
        _output = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var top = W.Stack(row, micRow, new Border { Height = 1, Background = UI.Theme.Track, Margin = new Thickness(0, 4, 0, 4) });
        var outputHeader = W.Text(L.T("Output"), UI.Theme.Small, UI.Theme.Dim);
        outputHeader.Margin = new Thickness(0, 6, 0, 0);
        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        // Apps first (what you reach for mid-game), then the outputs; the list scrolls when the tile is short.
        body.Children.Add(new ScrollViewer { Content = W.Stack(_apps, outputHeader, _outputs), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        View = MakeTile(body, _output);
    }

    /// <summary>Dragging fires many changes; passes on the latest at most ~8 times a second (each is a wpctl call).</summary>
    static Action<double> Throttled(Action<double> apply)
    {
        double? pending = null;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (pending is double v)
                apply(v);
            pending = null;
        };
        return v =>
        {
            pending = v;
            if (!timer.IsEnabled)
                timer.Start();
        };
    }

    /// <summary>One app: icon and name (the mute button), slider, value; laid out like the Master row.</summary>
    Control MakeAppRow(LinuxAppVolume app)
    {
        var audio = LinuxAudio.Shared;
        string id = app.Id;
        var slider = new TouchSlider();
        slider.Changed += Throttled(v => audio.SetAppVolume(id, v));
        var value = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        var name = W.Text(app.Name, UI.Theme.Small);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var icon = Icon.Make("volume", 15, UI.Theme.Dim);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Width = 26;
        var nameArea = new DockPanel { Background = Brushes.Transparent, MinHeight = 40 };
        DockPanel.SetDock(icon, Dock.Left);
        nameArea.Children.Add(icon);
        nameArea.Children.Add(name);
        Tap.On(nameArea, () => audio.SetAppMuted(id, !(audio.Apps.FirstOrDefault(a => a.Id == id)?.Muted ?? false)));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*,44"), Margin = new Thickness(0, 1) };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(nameArea);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        _appRows[id] = new AppRow(slider, value, icon);
        return grid;
    }

    public override void Update(Snapshot s)
    {
        // Reading the audio state keeps wpctl polling; a mixer on a hidden page doesn't need it (it refreshes when shown).
        if (!View.IsEffectivelyVisible)
            return;
        var audio = LinuxAudio.Shared;
        Icon.Set(_icon, audio.Muted == true ? "mute" : "volume");
        _master.Value = audio.Volume ?? 0;
        _value.Text = audio.Muted == true ? L.T("muted") : $"{(audio.Volume ?? 0) * 100:0}%";
        _master.Fill = audio.Muted == true ? UI.Theme.Dim : null;
        _mic.Text = L.T(audio.MicMuted == true ? "Microphone muted · tap to unmute" : "Microphone on · tap to mute");
        _mic.Foreground = audio.MicMuted == true ? UI.Theme.Crit : UI.Theme.Text;
        _output.Text = (audio.OutputName ?? "").Split(" (")[0];
        ShowApps(audio.Apps);
        var outputs = audio.Outputs;
        string key = string.Join("|", outputs.Select(o => $"{o.Id}:{o.IsDefault}"));
        if (key == _outputKey)
            return;
        _outputKey = key;
        _outputs.Children.Clear();
        foreach (var o in outputs)
        {
            string name = o.Name;
            var line = W.Text((o.IsDefault ? "●  " : "○  ") + name, UI.Theme.Small, o.IsDefault ? UI.Theme.Accent : UI.Theme.Text);
            line.Margin = new Thickness(0, 3);
            _outputs.Children.Add(Tap.On(new Border { Background = Brushes.Transparent, Child = line }, () => audio.NextOutput(name)));
        }
    }

    void ShowApps(IReadOnlyList<LinuxAppVolume> apps)
    {
        string key = string.Join(",", apps.Select(a => a.Id + ":" + a.Name));
        if (key != _appKey)
        {
            // Apps came or went: rebuild the rows (rare).
            _appKey = key;
            _apps.Children.Clear();
            _appRows.Clear();
            foreach (var a in apps)
                _apps.Children.Add(MakeAppRow(a));
            if (apps.Count == 0)
            {
                var none = W.Text(L.T("No app is playing sound."), UI.Theme.Small, UI.Theme.Dim);
                none.Margin = new Thickness(0, 4);
                _apps.Children.Add(none);
            }
        }
        foreach (var a in apps)
        {
            if (!_appRows.TryGetValue(a.Id, out var row))
                continue;
            var fill = a.Muted ? UI.Theme.Dim : null;
            if (row.Slider.Fill != fill || Math.Abs(row.Slider.Value - a.Volume) > 0.001)
            {
                row.Slider.Fill = fill;
                row.Slider.Value = a.Volume; // redraws only on a change (the dashboard renders in software)
            }
            row.Value.Text = a.Muted ? L.T("muted") : $"{a.Volume * 100:0}%";
            Icon.Set(row.Icon, a.Muted ? "mute" : "volume");
            row.Icon.Foreground = a.Muted ? UI.Theme.Crit : UI.Theme.Dim;
        }
    }
}

/// <summary>Pomodoro timer: focus / break with touch controls.</summary>
public sealed class TimerModule : Module
{
    readonly FocusTimer _timer;
    readonly TextBlock _time, _phase, _playIcon;
    readonly Meter _progress = new(6);

    public TimerModule(ModuleConfig config, LinuxCollector collector) : base(config, "Focus timer")
    {
        int focus = config.OptInt("Focus", 25), pause = config.OptInt("Break", 5);
        _timer = collector.Service($"timer:{focus}:{pause}", () => new FocusTimer(focus, pause));

        _time = W.Number("25:00", 54);
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        _phase = W.Text("", UI.Theme.Small, UI.Theme.Dim);
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
        var square = UI.Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        return Tap.On(new Border
        {
            Child = icon, Height = 44, Margin = new Thickness(3), Background = UI.Theme.Track,
            CornerRadius = new CornerRadius(square ? 0 : 8),
        }, action);
    }

    public override void Update(Snapshot s)
    {
        var left = _timer.Remaining;
        _time.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        _time.Foreground = _timer.OnBreak ? UI.Theme.Good : UI.Theme.Text;
        _phase.Text = L.T(_timer.OnBreak ? "break" : "focus") + (_timer.Running ? "" : L.T(" · paused")) +
                      (_timer.Completed > 0 ? L.F(" · {0} done", _timer.Completed) : "");
        Icon.Set(_playIcon, _timer.Running ? "pause" : "play");
        _progress.Set((1 - left.TotalSeconds / _timer.Length.TotalSeconds) * 100, _timer.OnBreak ? UI.Theme.Good : UI.Theme.Accent);
    }
}
