using System.Windows;
using System.Windows.Controls;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

public sealed class CpuModule : Module
{
    readonly TextBlock _name, _load, _temp, _tempCaption, _clock, _power, _cores, _extra, _extraLabel;
    readonly CoreBars _threads = new();
    readonly Sparkline _history = new("cpu.load");

    public CpuModule(ModuleConfig config) : base(config, "CPU")
    {
        _name = W.Text("", Theme.Small, Theme.Dim);
        var stats = W.Columns(W.Stat(L.T("load"), out _load), W.Stat(L.T("temp"), out _temp, out _tempCaption));
        var details = W.Stack(
            W.SpacedColumns(W.Row(L.T("Clock"), out _clock), W.Row(L.T("Power"), out _power)),
            W.SpacedColumns(W.Row(L.T("Cores"), out _cores), W.Row(L.T("Voltage"), out _extra, out _extraLabel)));
        details.Margin = new Thickness(0, 6, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, details.ShowFrom(Density.Normal), _threads.ShowFrom(Density.Normal)), _name);
    }

    public override void Update(Snapshot s)
    {
        var c = s.Cpu;
        if (c == null)
            return;

        _name.Text = c.Name;
        _load.Text = W.Format(c.LoadPercent, "0", "%");
        _temp.Text = W.Temp(c.TemperatureC);
        _temp.Foreground = Theme.ForTemperature(c.TemperatureC);

        // Say *why* there's no temperature instead of a bare dash.
        _tempCaption.Text = c.SensorHint == null ? L.T("temp") : L.F("temp · {0}", L.T(c.SensorHint));
        _tempCaption.Foreground = Hint(c.SensorHint, "needs admin") || Hint(c.SensorHint, "needs PawnIO") ? Theme.Warn : Theme.Dim;
        _temp.ToolTip = Hint(c.SensorHint, "needs PawnIO") ? L.T("Install the PawnIO driver: tray icon → Enable CPU temperature.")
            : Hint(c.SensorHint, "needs admin") ? L.T("Run Shoji as administrator: tray icon → Enable CPU temperature.")
            : null;

        _clock.Text = c.ClockMhz is double mhz ? (mhz / 1000).ToString("0.00") + " GHz" : "–";
        _power.Text = W.Format(c.PowerW, "0", " W");
        _cores.Text = c.Cores is int n ? $"{n}C · {c.Threads}T" : L.F("{0} threads", c.Threads);

        // Voltage needs the driver; without it show the base clock rather than an empty row.
        if (c.VoltageV is double v)
        {
            _extraLabel.Text = L.T("Voltage");
            _extra.Text = $"{v:0.000} V";
        }
        else
        {
            _extraLabel.Text = L.T("Base");
            _extra.Text = c.BaseMhz is double b ? (b / 1000).ToString("0.00") + " GHz" : "–";
        }

        _threads.Set(c.CoreLoads);
        _history.Refresh();
    }

    /// <summary>The sensor hint, which may come in English or already translated.</summary>
    static bool Hint(string? hint, string english) => hint != null && (hint == english || hint == L.T(english));
}

public sealed class GpuModule : Module
{
    readonly TextBlock _name, _load, _temp, _vram, _clock, _fan, _power, _hotspot;
    readonly Meter _vramBar = new();
    readonly Sparkline _history = new("gpu.load");

    public GpuModule(ModuleConfig config) : base(config, "GPU")
    {
        _name = W.Text("", Theme.Small, Theme.Dim);
        var stats = W.Columns(W.Stat(L.T("load"), out _load), W.Stat(L.T("temp"), out _temp));
        var vramRow = W.Row("VRAM", out _vram);
        vramRow.Margin = new Thickness(0, 6, 0, 0);
        var details = W.SpacedColumns(
            W.Stack(W.Row(L.T("Core"), out _clock), W.Row(L.T("Fan"), out _fan)),
            W.Stack(W.Row(L.T("Power"), out _power), W.Row(L.T("Hot spot"), out _hotspot)));
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, vramRow.ShowFrom(Density.Normal), _vramBar.ShowFrom(Density.Normal), details.ShowFrom(Density.Normal)), _name);
    }

    public override void Update(Snapshot s)
    {
        var g = s.Gpu;
        if (g == null)
            return;

        _name.Text = g.Name;
        _load.Text = W.Format(g.LoadPercent, "0", "%");
        _temp.Text = W.Temp(g.TemperatureC);
        _temp.Foreground = Theme.ForTemperature(g.TemperatureC);
        _hotspot.Text = W.Temp(g.HotSpotC, unit: true);
        _hotspot.Foreground = Theme.ForTemperature(g.HotSpotC);

        if (g.VramUsedMb is double used && g.VramTotalMb is double total && total > 0)
        {
            _vram.Text = $"{used / 1024:0.0} / {total / 1024:0} GB";
            _vramBar.Set(used / total * 100, Theme.ForPercent(used / total * 100));
        }
        else
        {
            _vram.Text = W.Format(g.VramUsedMb / 1024, "0.0", " GB");
        }

        _clock.Text = W.Format(g.CoreClockMhz, "0", " MHz");
        _fan.Text = g.FanRpm is > 0 ? $"{g.FanRpm:0} rpm" : g.FanPercent is > 0 ? $"{g.FanPercent:0}%" : L.T("idle");
        _power.Text = W.Format(g.PowerW, "0", " W");
        _history.Refresh();
    }
}

public sealed class MemoryModule : Module
{
    readonly TextBlock _used, _usedCaption, _percent, _commit;
    readonly Meter _bar = new(8);
    readonly Sparkline _history = new("mem.load");

    public MemoryModule(ModuleConfig config) : base(config, "Memory")
    {
        var stats = W.Columns(W.Stat(L.T("used"), out _used, out _usedCaption), W.Stat(L.T("load"), out _percent));
        var commit = W.Row(L.T("Committed"), out _commit);
        commit.Margin = new Thickness(0, 4, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, _bar, commit.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var m = s.Memory;
        if (m == null)
            return;

        _used.Text = $"{m.UsedGb:0.0}";
        _used.ToolTip = L.F("{0:0.0} of {1:0.0} GB", m.UsedGb, m.TotalGb);
        _usedCaption.Text = L.F("GB used of {0:0}", m.TotalGb);
        _percent.Text = $"{m.LoadPercent:0}%";
        _percent.Foreground = m.LoadPercent >= 90 ? Theme.Crit : Theme.Text;
        _bar.Set(m.LoadPercent, Theme.ForPercent(m.LoadPercent));
        _commit.Text = $"{m.CommitUsedGb:0.0} / {m.CommitTotalGb:0} GB";
        _history.Refresh();
    }
}

public sealed class NetworkModule : Module
{
    readonly TextBlock _down, _up, _adapter, _ip, _totals;
    readonly Sparkline _history = new("net.down", "net.up", null, Theme.Accent, Theme.Upload);

    public NetworkModule(ModuleConfig config) : base(config, "Network")
    {
        _adapter = W.Text("", Theme.Small, Theme.Dim);
        var stats = W.Columns(W.Stat(L.T("▼ download"), out _down, Theme.Medium + 5), W.Stat(L.T("▲ upload"), out _up, Theme.Medium + 5));
        _up.Foreground = Theme.Upload;
        var details = W.SpacedColumns(W.Row("IP", out _ip), W.Row(L.T("Session"), out _totals));
        details.Margin = new Thickness(0, 6, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, details.ShowFrom(Density.Normal)), _adapter);
    }

    public override void Update(Snapshot s)
    {
        var n = s.Network;
        if (n == null)
        {
            _down.Text = _up.Text = L.T("offline");
            return;
        }

        _adapter.Text = n.AdapterName;
        _down.Text = W.Rate(n.DownloadBytesPerSec);
        _up.Text = W.Rate(n.UploadBytesPerSec);
        _ip.Text = n.IpAddress ?? "–";
        _totals.Text = $"▼{W.Bytes(n.TotalDownloadedBytes)} ▲{W.Bytes(n.TotalUploadedBytes)}";
        _history.Refresh();
    }
}
