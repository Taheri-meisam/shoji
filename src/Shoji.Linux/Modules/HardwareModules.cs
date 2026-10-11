using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
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
        _name = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var stats = W.Columns(W.Stat("load", out _load), W.Stat("temp", out _temp, out _tempCaption));
        var details = W.Stack(
            W.SpacedColumns(W.Row("Clock", out _clock), W.Row("Power", out _power)),
            W.SpacedColumns(W.Row("Cores", out _cores), W.Row("Voltage", out _extra, out _extraLabel)));
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
        _temp.Foreground = UI.Theme.ForTemperature(c.TemperatureC);

        // Say *why* there's no temperature instead of a bare dash.
        _tempCaption.Text = c.SensorHint == null || c.TemperatureC != null ? L.T("temp") : L.T("temp") + " · " + c.SensorHint;
        _tempCaption.Foreground = c.SensorHint is "needs admin" or "needs PawnIO" ? UI.Theme.Warn : UI.Theme.Dim;
        ToolTip.SetTip(_temp, c.SensorHint == null ? null : L.T("See Settings → Hardware sensors"));

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
}

public sealed class GpuModule : Module
{
    readonly TextBlock _name, _load, _temp, _vram, _clock, _fan, _power, _hotspot;
    readonly Meter _vramBar = new();
    readonly Sparkline _history = new("gpu.load");

    public GpuModule(ModuleConfig config) : base(config, "GPU")
    {
        _name = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var stats = W.Columns(W.Stat("load", out _load), W.Stat("temp", out _temp));
        var vramRow = W.Row("VRAM", out _vram);
        vramRow.Margin = new Thickness(0, 6, 0, 0);
        var details = W.SpacedColumns(
            W.Stack(W.Row("Core", out _clock), W.Row("Fan", out _fan)),
            W.Stack(W.Row("Power", out _power), W.Row("Hot spot", out _hotspot)));
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
        _temp.Foreground = UI.Theme.ForTemperature(g.TemperatureC);
        _hotspot.Text = W.Temp(g.HotSpotC, unit: true);
        _hotspot.Foreground = UI.Theme.ForTemperature(g.HotSpotC);

        if (g.VramUsedMb is double used && g.VramTotalMb is double total && total > 0)
        {
            _vram.Text = $"{used / 1024:0.0} / {total / 1024:0} GB";
            _vramBar.Set(used / total * 100, UI.Theme.ForPercent(used / total * 100));
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
        var stats = W.Columns(W.Stat("used", out _used, out _usedCaption), W.Stat("load", out _percent));
        var commit = W.Row("Committed", out _commit);
        commit.Margin = new Thickness(0, 4, 0, 0);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stats, _bar, commit.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var m = s.Memory;
        if (m == null)
            return;

        _used.Text = $"{m.UsedGb:0.0}";
        ToolTip.SetTip(_used, L.F("{0:0.0} of {1:0.0} GB", m.UsedGb, m.TotalGb));
        _usedCaption.Text = L.F("GB used of {0:0}", m.TotalGb);
        _percent.Text = $"{m.LoadPercent:0}%";
        _percent.Foreground = m.LoadPercent >= 90 ? UI.Theme.Crit : UI.Theme.Text;
        _bar.Set(m.LoadPercent, UI.Theme.ForPercent(m.LoadPercent));
        _commit.Text = $"{m.CommitUsedGb:0.0} / {m.CommitTotalGb:0} GB";
        _history.Refresh();
    }
}

public sealed class NetworkModule : Module
{
    readonly TextBlock _down, _up, _adapter, _ip, _totals;
    readonly Sparkline _history = new("net.down", "net.up", null, UI.Theme.Accent, UI.Theme.Upload);

    public NetworkModule(ModuleConfig config) : base(config, "Network")
    {
        _adapter = W.Text("", UI.Theme.Small, UI.Theme.Dim);
        var stats = W.Columns(W.Stat("▼ download", out _down, UI.Theme.Medium + 5), W.Stat("▲ upload", out _up, UI.Theme.Medium + 5));
        _up.Foreground = UI.Theme.Upload;
        var details = W.SpacedColumns(W.Row("IP", out _ip), W.Row("Session", out _totals));
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
