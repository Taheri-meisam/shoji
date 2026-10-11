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

namespace Shoji.Modules;

// Gauge versions of the tiles (Style setting / theme default). The "Classic" versions live in the other files.

/// <summary>Builds the single-value gauges shared by several tiles.</summary>
static class Gauges
{
    public static Gauge Load(string style, string label, string seal = "負荷") => style switch
    {
        "led" => new LedArc { Label = label, WarnAt = 75, CritAt = 90 },
        "enso" => new EnsoGauge { Seal = seal },
        "speedometer" => new Speedometer { Label = label, Max = 100, Major = 20, Minor = 5, Redline = 90 },
        _ => new ArcGauge { Label = label },
    };

    /// <summary>Temperature in the configured unit, for LED digits ("64.2").</summary>
    public static string TempDigits(double? celsius) =>
        celsius is double c ? (Theme.Fahrenheit ? c * 9 / 5 + 32 : c).ToString("0.0") : "--.-";

    public static string TempUnit => Theme.Fahrenheit ? "°F" : "°C";

    /// <summary>A °C value in the configured unit.</summary>
    public static double Unit(double celsius) => Theme.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public static string Join(params string?[] parts) => string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
}

public sealed class CpuGaugeModule : Module
{
    readonly string _style;
    readonly Gauge _gauge;
    readonly TextBlock _name, _info = InfoLine();
    bool _scaled;

    public CpuGaugeModule(ModuleConfig config, string style) : base(config, "CPU")
    {
        _style = style;
        _name = W.Text("", Theme.Small, Theme.Dim);
        _gauge = style switch
        {
            "speedometer" => new Speedometer { Label = "GHz", Max = 6, Major = 1, Minor = 0.25, Redline = 5.4, Format = v => v.ToString("0.00") },
            "segments" => new SevenSegment(),
            "hex" => new HexGrid(),
            _ => Gauges.Load(style, L.T("CPU LOAD")),
        };
        View = GaugeTile(_gauge, _info, _name);
    }

    public override void Update(Snapshot s)
    {
        var c = s.Cpu;
        if (c == null)
            return;
        _name.Text = c.Name;
        string temp = c.TemperatureC != null ? W.Temp(c.TemperatureC) : L.F("temp {0}", L.T(c.SensorHint ?? "–"));
        string clock = c.ClockMhz is double mhz ? $"{mhz / 1000:0.00} GHz" : "";
        string load = W.Format(c.LoadPercent, "0", "%");

        switch (_gauge)
        {
            case Speedometer sp:
                // Scale the dial to this CPU once: a bit above its boost clock, redline near the top.
                if (!_scaled && c.BaseMhz is double b)
                {
                    sp.Max = Math.Max(4, Math.Ceiling(b * 1.65 / 1000));
                    sp.Redline = sp.Max * 0.9;
                    _scaled = true;
                }
                sp.Set("v", c.ClockMhz / 1000);
                _info.Text = Gauges.Join(load, temp, W.Format(c.PowerW, "0", " W"));
                break;
            case SevenSegment seg:
                if (c.TemperatureC != null)
                {
                    seg.Value = Gauges.TempDigits(c.TemperatureC);
                    seg.Label = "CPU " + Gauges.TempUnit;
                    seg.Color = c.TemperatureC >= Theme.TempCrit ? Theme.Crit : c.TemperatureC >= Theme.TempWarn ? Theme.Warn : null;
                }
                else
                {
                    seg.Value = W.Format(c.LoadPercent, "0", "").PadLeft(3);
                    seg.Label = L.T("CPU LOAD %");
                    seg.Color = null;
                }
                _info.Text = Gauges.Join(c.TemperatureC != null ? load : temp, clock);
                break;
            case HexGrid hex:
                hex.SetLoads(c.CoreLoads);
                _info.Text = Gauges.Join(load, temp, clock);
                break;
            default:
                _gauge.Set("v", c.LoadPercent);
                _info.Text = Gauges.Join(temp, clock, W.Format(c.PowerW, "0", " W"));
                break;
        }
        _info.Foreground = c.TemperatureC == null && c.SensorHint is { } hint && (hint is "needs admin" or "needs PawnIO" || hint == L.T("needs admin") || hint == L.T("needs PawnIO")) ? Theme.Warn : Theme.Dim;
    }
}

public sealed class GpuGaugeModule : Module
{
    readonly Gauge _gauge;
    readonly TextBlock _name, _info = InfoLine();

    public GpuGaugeModule(ModuleConfig config, string style) : base(config, "GPU")
    {
        _name = W.Text("", Theme.Small, Theme.Dim);
        _gauge = style == "segments" ? new SevenSegment() : Gauges.Load(style, L.T("GPU LOAD"), "画像");
        View = GaugeTile(_gauge, _info, _name);
    }

    public override void Update(Snapshot s)
    {
        var g = s.Gpu;
        if (g == null)
            return;
        _name.Text = g.Name;
        string vram = g.VramUsedMb is double u && g.VramTotalMb is double t && t > 0 ? $"{u / 1024:0.0}/{t / 1024:0} GB" : "";
        if (_gauge is SevenSegment seg)
        {
            seg.Value = Gauges.TempDigits(g.TemperatureC);
            seg.Label = "GPU " + Gauges.TempUnit;
            seg.Color = g.TemperatureC >= Theme.TempCrit ? Theme.Crit : g.TemperatureC >= Theme.TempWarn ? Theme.Warn : null;
            _info.Text = Gauges.Join(W.Format(g.LoadPercent, "0", "%"), W.Format(g.PowerW, "0", " W"), vram);
            return;
        }
        _gauge.Set("v", g.LoadPercent);
        _info.Text = Gauges.Join(W.Temp(g.TemperatureC), W.Format(g.CoreClockMhz, "0", " MHz"), W.Format(g.PowerW, "0", " W"), vram);
    }
}

public sealed class MemoryGaugeModule : Module
{
    readonly Gauge _gauge;
    readonly TextBlock _info = InfoLine();

    public MemoryGaugeModule(ModuleConfig config, string style) : base(config, "Memory")
    {
        if (style == "rings")
        {
            var rings = new RingsGauge();
            rings.Items.Add(("RAM", Theme.Accent));
            rings.Items.Add((L.T("COMMIT"), Theme.Accent2));
            _gauge = rings;
        }
        else
        {
            _gauge = Gauges.Load(style, L.T("RAM IN USE"), "記憶");
        }
        View = GaugeTile(_gauge, _info);
    }

    public override void Update(Snapshot s)
    {
        var m = s.Memory;
        if (m == null)
            return;
        if (_gauge is RingsGauge)
        {
            _gauge.Set("i0", m.LoadPercent);
            _gauge.Set("i1", m.CommitTotalGb > 0 ? m.CommitUsedGb / m.CommitTotalGb * 100 : null);
        }
        else
        {
            _gauge.Set("v", m.LoadPercent);
        }
        _info.Text = L.F("{0:0.0} / {1:0} GB  ·  committed {2:0} / {3:0} GB", m.UsedGb, m.TotalGb, m.CommitUsedGb, m.CommitTotalGb);
    }
}

public sealed class FpsGaugeModule : Module
{
    readonly Gauge _gauge;
    readonly TextBlock _app, _info = InfoLine();

    public FpsGaugeModule(ModuleConfig config, string style) : base(config, "FPS")
    {
        _app = W.Text("", Theme.Small, Theme.Dim);
        _gauge = style switch
        {
            // FPS reads like speed; the red zone is at the slow end.
            "speedometer" => new Speedometer { Label = "FPS", Max = 180, Major = 20, Minor = 5, Redline = 30, RedlineIsLow = true },
            "tape" => new TapeGauge { LowIsBad = true, WarnAt = 60, CritAt = 30, Span = 80, Step = 5, LabelEvery = 20, Unit = "FPS" },
            "segments" => new SevenSegment { Label = "FPS" },
            _ => new ArcGauge { Label = "FPS", Unit = "", Max = 180, WarnAt = 1e9, CritAt = 1e9 },
        };
        View = GaugeTile(_gauge, _info, _app);
    }

    public override void Update(Snapshot s)
    {
        var f = s.Fps;
        _app.Text = s.Foreground.ProcessName;
        if (f?.Error != null)
        {
            _info.Text = f.Error == "needs admin" || f.Error == L.T("needs admin") ? L.T("FPS needs admin: tray → Restart as admin") : L.F("FPS counter {0}", L.T(f.Error));
            _info.Foreground = Theme.Warn;
            return;
        }
        _info.Foreground = Theme.Dim;
        _info.Text = Gauges.Join(f?.OnePercentLow is double low ? L.F("1% low {0:0}", low) : null, f?.FrameTimeMs is double ft ? $"{ft:0.0} ms" : null);

        // Fast monitors: widen the dial instead of pinning the needle.
        if (f?.Fps is double now && now > 170)
        {
            // Only ever widen, so the scale doesn't flip back and forth around a threshold.
            if (_gauge is Speedometer sp && sp.Max < 360) { sp.Max = Math.Max(sp.Max, Math.Min(360, Math.Ceiling(now / 60) * 60 + 60)); sp.Major = sp.Max > 240 ? 40 : 20; }
            if (_gauge is ArcGauge arc && arc.Max < now) arc.Max = Math.Ceiling(now / 60) * 60 + 60;
        }

        switch (_gauge)
        {
            case TapeGauge tape: tape.Push(f?.Fps); break;
            case SevenSegment seg:
                seg.Value = f?.Fps is double v ? v.ToString("0").PadLeft(3) : "---";
                seg.Color = f?.Fps < 30 ? Theme.Crit : f?.Fps < 60 ? Theme.Warn : null;
                break;
            default: _gauge.Set("v", f?.Fps); break;
        }
    }
}

/// <summary>Temperatures as flight tapes, coolant tubes or LED digits of the hottest sensor.</summary>
public sealed class TempsGaugeModule : Module
{
    readonly string _style;
    readonly UniformGrid _tapes = new() { Rows = 1 };
    readonly TubesGauge? _tubes;
    readonly SevenSegment? _seg;
    readonly TextBlock _info = InfoLine();
    string _labels = "";

    public TempsGaugeModule(ModuleConfig config, string style) : base(config, "Temperatures")
    {
        _style = style;
        UIElement body = style switch
        {
            "tubes" => _tubes = new TubesGauge(),
            "segments" => _seg = new SevenSegment(),
            _ => _tapes,
        };
        View = GaugeTile(body, _info);
    }

    static string Short(string label) => label.Length <= 9 ? label : label[..9];

    public override void Update(Snapshot s)
    {
        var temps = s.Temps;
        if (temps.Count == 0)
        {
            _info.Text = HardwareSensors.DriverReady ? L.T("No temperature sensors found") : L.F("CPU and board sensors {0}", HardwareSensors.DriverHint);
            return;
        }

        if (_seg != null)
        {
            var hot = temps.MaxBy(t => t.Value)!;
            _seg.Value = Gauges.TempDigits(hot.Value);
            _seg.Label = $"{hot.Label} {Gauges.TempUnit}";
            _seg.Color = hot.Value >= Theme.TempCrit ? Theme.Crit : hot.Value >= Theme.TempWarn ? Theme.Warn : null;
            _info.Text = string.Join("  ·  ", temps.Where(t => t != hot).Take(3).Select(t => $"{t.Label} {W.Temp(t.Value)}"));
            return;
        }

        int max = _style == "tubes" ? 5 : 4;
        var shown = temps.Take(max).ToList();
        string labels = string.Join("|", shown.Select(t => t.Label));
        if (labels != _labels)
        {
            // Sensors appeared or went away: rebuild (rare).
            _labels = labels;
            if (_tubes != null)
            {
                _tubes.Labels.Clear();
                _tubes.Labels.AddRange(shown.Select(t => Short(t.Label)));
            }
            else
            {
                _tapes.Children.Clear();
                foreach (var t in shown)
                    _tapes.Children.Add(new TapeGauge { Label = Short(t.Label), Unit = Gauges.TempUnit, WarnAt = Gauges.Unit(Theme.TempWarn), CritAt = Gauges.Unit(Theme.TempCrit) });
            }
        }
        for (int i = 0; i < shown.Count; i++)
        {
            if (_tubes != null)
                _tubes.Set("i" + i, shown[i].Value);
            else if (_tapes.Children[i] is TapeGauge tape)
                tape.Push(Gauges.Unit(shown[i].Value)); // the tape's scale is in the display unit
        }
        _info.Text = temps.Count > max ? L.F("+{0} more · max {1}", temps.Count - max, W.Temp(temps.Max(t => t.Value))) : L.F("max {0}", W.Temp(temps.Max(t => t.Value)));
    }
}

public sealed class NetworkScopeModule : Module
{
    readonly ScopeGauge _scope = new() { SeriesA = "net.down", SeriesB = "net.up", Format = W.Rate, Caption = L.T("▼ download  ▲ upload") };
    readonly TextBlock _adapter, _info = InfoLine();

    public NetworkScopeModule(ModuleConfig config) : base(config, "Network")
    {
        _adapter = W.Text("", Theme.Small, Theme.Dim);
        View = GaugeTile(_scope, _info, _adapter);
    }

    public override void Update(Snapshot s)
    {
        var n = s.Network;
        if (n == null)
        {
            _info.Text = L.T("offline");
            return;
        }
        _adapter.Text = n.AdapterName;
        _info.Text = Gauges.Join("▼ " + W.Rate(n.DownloadBytesPerSec), "▲ " + W.Rate(n.UploadBytesPerSec), n.IpAddress);
        _scope.Refresh();
    }
}

public sealed class PingGaugeModule : Module
{
    readonly ScopeGauge? _scope;
    readonly SevenSegment? _seg;
    readonly TextBlock _info = InfoLine();

    public PingGaugeModule(ModuleConfig config, string style) : base(config, "Ping")
    {
        UIElement body = style == "segments"
            ? _seg = new SevenSegment { Label = "PING ms" }
            : _scope = new ScopeGauge { SeriesA = "ping", Format = v => $"{v:0} ms", Caption = L.T("latency") };
        View = GaugeTile(body, _info);
    }

    public override void Update(Snapshot s)
    {
        var p = s.Ping;
        if (p == null)
            return;
        _info.Text = Gauges.Join(p.Host, p.AverageMs is double a ? L.F("avg {0:0} ms", a) : null, p.JitterMs is double j ? L.F("jitter {0:0.0}", j) : null, L.F("loss {0:0}%", p.LossPercent));
        if (_seg != null)
        {
            _seg.Value = p.LastMs is double ms ? Math.Min(999, ms).ToString("0").PadLeft(3) : "---";
            _seg.Color = p.LastMs is null or >= 250 ? Theme.Crit : p.LastMs >= 100 ? Theme.Warn : null;
        }
        _scope?.Refresh();
    }
}

public sealed class ProcessRadarModule : Module
{
    readonly RadarGauge _radar = new();

    public ProcessRadarModule(ModuleConfig config) : base(config, "Top processes")
    {
        View = GaugeTile(_radar, null, W.Text(L.T("closer = busier"), Theme.Small, Theme.Dim));
    }

    public override void Update(Snapshot s) => _radar.SetBlips(s.TopProcesses.Select(p => (p.Name, p.CpuPercent)));
}

public sealed class ClockSegmentsModule : Module
{
    readonly SevenSegment _seg = new();
    readonly TextBlock _info = InfoLine();
    readonly bool _use24Hour, _weather;
    readonly AgendaService? _agenda;

    public ClockSegmentsModule(ModuleConfig config, bool use24Hour, AgendaService? agenda = null) : base(config, "Today")
    {
        _use24Hour = use24Hour;
        _agenda = agenda;
        _weather = !config.OptBool("HideWeather");
        _info.FontSize = Theme.Medium;
        _info.Foreground = Theme.Text;
        View = GaugeTile(_seg, _info, infoAlways: true);
    }

    public override void Update(Snapshot s)
    {
        // Invariant culture: ':' in a format string means the locale's time separator, which isn't always a colon.
        _seg.Value = s.Time.ToString(_use24Hour ? "HH:mm" : "h:mm", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
        _seg.InvalidateVisual(); // the colon blinks once a second
        _seg.Label = _use24Hour ? "" : s.Time.ToString("tt");
        // The next calendar event, when there is one, says more than the uptime.
        string? next = _agenda == null ? null : CalendarFeeds.NextLine(_agenda.State, s.Time, _use24Hour);
        _info.Text = Gauges.Join(s.Time.ToString(L.T("dddd, d MMMM"), L.Culture), _weather ? WeatherBadge.Line(s.Weather) : null, next ?? L.F("up {0}", W.Duration(s.Uptime)));
    }
}

public sealed class BatteryGaugeModule : Module
{
    readonly Gauge _gauge;
    readonly TextBlock _info = InfoLine();

    public BatteryGaugeModule(ModuleConfig config, string style) : base(config, "Battery")
    {
        _gauge = style == "led" ? new LedArc { Label = L.T("CHARGE"), WarnAt = 1e9, CritAt = 1e9 } : new ArcGauge { Label = L.T("CHARGE"), WarnAt = 1e9, CritAt = 1e9 };
        View = GaugeTile(_gauge, _info);
    }

    public override void Update(Snapshot s)
    {
        var b = s.Battery;
        if (b == null)
            return;
        if (!b.HasBattery)
        {
            _info.Text = L.T("No battery (mains power)");
            return;
        }
        _gauge.Set("v", b.Percent);
        _info.Text = Gauges.Join(b.Charging ? L.T("charging") : b.PluggedIn ? L.T("plugged in") : L.T("on battery"),
            !b.PluggedIn && b.Remaining is TimeSpan t ? L.F("{0} left", W.Duration(t)) : null);
        _info.Foreground = !b.PluggedIn && b.Percent <= 15 ? Theme.Crit : Theme.Dim;
    }
}

public sealed class TimerGaugeModule : Module
{
    readonly FocusTimer _timer;
    readonly Gauge _gauge;
    readonly TextBlock _phase = InfoLine(), _playIcon;

    public TimerGaugeModule(ModuleConfig config, Collector collector, string style) : base(config, "Focus timer")
    {
        int focus = config.OptInt("Focus", 25), pause = config.OptInt("Break", 5);
        _timer = collector.Service($"timer:{focus}:{pause}", () => new FocusTimer(focus, pause));
        string Left(double _) { var l = _timer.Remaining; return $"{(int)l.TotalMinutes:00}:{l.Seconds:00}"; }
        _gauge = style == "enso"
            ? new EnsoGauge { Seal = "集中", Format = Left }
            : new ArcGauge { Label = L.T("FOCUS"), Unit = "", WarnAt = 1e9, CritAt = 1e9, Format = Left };

        _playIcon = Icon.Make("play", 20);
        var controls = W.Columns(Button(_playIcon, _timer.StartPause), Button(Icon.Make("refresh", 20), _timer.Reset), Button(Icon.Make("next", 20), _timer.Skip));
        controls.Margin = new Thickness(0, 4, 0, 0);
        var bottom = W.Stack(_phase, controls);
        var d = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        d.Children.Add(bottom);
        d.Children.Add(_gauge);
        View = MakeTile(d);
    }

    static Border Button(TextBlock icon, Action action)
    {
        bool square = Theme.Current.Shape is TileShape.Hud or TileShape.Chamfer;
        return Tap.On(new Border { Child = icon, Height = 44, Margin = new Thickness(3), Background = Theme.Track, CornerRadius = new CornerRadius(square ? 0 : 8) }, action);
    }

    public override void Update(Snapshot s)
    {
        var left = _timer.Remaining;
        _gauge.Set("v", (1 - left.TotalSeconds / _timer.Length.TotalSeconds) * 100);
        if (_gauge is ArcGauge arc)
            arc.Label = _timer.OnBreak ? L.T("BREAK") : L.T("FOCUS");
        _gauge.InvalidateVisual(); // the time text changes every second even when progress barely moves
        _phase.Text = (_timer.OnBreak ? L.T("break") : L.T("focus")) + (_timer.Running ? "" : L.T(" · paused")) + (_timer.Completed > 0 ? L.F(" · {0} done", _timer.Completed) : "");
        Icon.Set(_playIcon, _timer.Running ? "pause" : "play");
    }
}

/// <summary>Caution / warning lamps lit by the alert limits — the cockpit way of saying "look here".</summary>
public sealed class StatusModule : Module
{
    readonly Annunciator _panel = new();
    readonly AlertsConfig _alerts;
    // Microphone, battery and network change rarely: read them every few seconds, not every refresh.
    DateTime _nextSlowRead;
    bool? _micMuted;
    BatteryInfo? _battery;
    volatile bool _networkUp = true;

    static readonly string[] VirtualAdapters =
        { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "docker", "wsl", "npcap", "loopback", "pseudo" };

    public StatusModule(ModuleConfig config, AppConfig app) : base(config, "Status")
    {
        _alerts = app.Alerts;
        // A tap acknowledges the lit cautions (they stay lit, the master lights stop blinking).
        View = GaugeTile(Tap.On(_panel, _panel.Acknowledge));
    }

    /// <summary>
    /// True when a real (not loopback / virtual / tunnel) adapter is up and has a default gateway.
    /// GetIsNetworkAvailable() is true whenever any adapter is up, including Hyper-V / VPN / WSL ones.
    /// </summary>
    static bool NetworkUp()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;
                string name = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (VirtualAdapters.Any(name.Contains))
                    continue;
                if (nic.GetIPProperties().GatewayAddresses.Any(g => g.Address is { } a
                        && !a.Equals(System.Net.IPAddress.Any) && !a.Equals(System.Net.IPAddress.IPv6Any)))
                    return true;
            }
            return false;
        }
        catch
        {
            return true; // can't tell: don't raise an alarm
        }
    }

    public override void Update(Snapshot s)
    {
        var a = _alerts;
        static int Level(double? v, double caution, double warning) => v >= warning ? 2 : v >= caution ? 1 : 0;
        double? lowestFree = s.Disks.Count == 0 ? null : s.Disks.Min(d => d.TotalGb > 0 ? (d.TotalGb - d.UsedGb) / d.TotalGb * 100 : 100);
        if (DateTime.UtcNow >= _nextSlowRead)
        {
            _nextSlowRead = DateTime.UtcNow.AddSeconds(5);
            try { _micMuted = AudioService.Shared.MicMuted; } catch { _micMuted = null; /* no microphone */ }
            if (s.Battery == null)
                try { _battery = SystemStats.ReadBattery(); } catch { _battery = null; }
            _ = System.Threading.Tasks.Task.Run(() => _networkUp = NetworkUp());
        }
        bool? micMuted = _micMuted;
        var battery = s.Battery ?? _battery;

        _panel.SetLamps(new (string, int)[]
        {
            (L.T("CPU HOT"), a.CpuTemp > 0 ? Level(s.Cpu?.TemperatureC, Theme.TempWarn, a.CpuTemp) : 0),
            (L.T("GPU HOT"), a.GpuTemp > 0 ? Level(s.Gpu?.TemperatureC, Theme.TempWarn, a.GpuTemp) : 0),
            (L.T("RAM HIGH"), a.RamPercent > 0 ? Level(s.Memory?.LoadPercent, a.RamPercent - 10, a.RamPercent) : 0),
            (L.T("PING HIGH"), a.PingMs > 0 && s.Ping != null ? (s.Ping.LastMs == null ? 2 : Level(s.Ping.LastMs, a.PingMs / 2, a.PingMs)) : 0),
            (L.T("DISK LOW"), a.DiskFreePercent > 0 && lowestFree is double free ? (free <= a.DiskFreePercent ? 2 : free <= a.DiskFreePercent * 2 ? 1 : 0) : 0),
            (L.T("NET DOWN"), _networkUp ? 0 : 2),
            (L.T("SITE DOWN"), s.Sites.Any(x => x.State == SiteState.Down) ? 2 : s.Sites.Any(x => x.State == SiteState.Warning) ? 1 : 0),
            // Information lamps (steady, no master caution): an open mic and battery power are normal states.
            (L.T("MIC LIVE"), micMuted == false ? 3 : 0),
            (L.T("ON BATTERY"), battery is { HasBattery: true, PluggedIn: false } ? (battery.Percent <= 15 ? 2 : 3) : 0),
        });
    }
}

/// <summary>The whole PC in one tile: orbiting planets or rings for CPU, GPU, RAM and network.</summary>
public sealed class OverviewModule : Module
{
    readonly Gauge _gauge;

    public OverviewModule(ModuleConfig config, string style) : base(config, "System")
    {
        if (style == "rings")
        {
            var rings = new RingsGauge();
            rings.Items.Add(("CPU", Theme.Accent));
            rings.Items.Add(("GPU", Theme.Accent2));
            rings.Items.Add(("RAM", Theme.Good));
            _gauge = rings;
        }
        else
        {
            var orbit = new OrbitGauge();
            orbit.Bodies.Add(("CPU", Theme.Accent, "%"));
            orbit.Bodies.Add(("GPU", Theme.Accent2, "%"));
            orbit.Bodies.Add(("RAM", Theme.Good, "%"));
            orbit.Bodies.Add((L.T("NET"), Theme.Warn, ""));
            _gauge = orbit;
        }
        View = GaugeTile(_gauge);
    }

    public override void Update(Snapshot s)
    {
        _gauge.Set("i0", s.Cpu?.LoadPercent);
        _gauge.Set("i1", s.Gpu?.LoadPercent);
        _gauge.Set("i2", s.Memory?.LoadPercent);
        // Network as a share of 1 Gbit/s, so the planet speeds up with traffic.
        if (s.Network is { } n)
            _gauge.Set("i3", Math.Min(100, n.DownloadBytesPerSec * 8 / 1e7));
    }
}
