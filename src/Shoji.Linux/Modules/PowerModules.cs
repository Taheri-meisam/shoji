using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Shared bits of the power tiles: energy and cost text.</summary>
static class PowerText
{
    public static string Energy(double wh) => wh >= 1000 ? $"{wh / 1000:0.00} kWh" : $"{wh:0} Wh";

    /// <summary>"€0.55" when a price per kWh is set, else null.</summary>
    public static string? Cost(ModuleConfig c, double wh)
    {
        if (!double.TryParse(c.Opt("Price"), NumberStyles.Float, CultureInfo.CurrentCulture, out double price)
            && !double.TryParse(c.Opt("Price"), NumberStyles.Float, CultureInfo.InvariantCulture, out price))
            return null;
        string symbol = c.Opt("Currency") ?? RegionInfo.CurrentRegion.CurrencySymbol;
        return $"{symbol}{wh / 1000 * price:0.00}";
    }

    public static IBrush BrushFor(PowerPart p) => p.Label switch
    {
        "CPU" => UI.Theme.Accent,
        "GPU" or "GPUs" => UI.Theme.Accent2,
        "Rest of PC" => UI.Theme.Good,
        _ => UI.Theme.Dim,
    };
}

/// <summary>Whole-system power: total, what it's made of, energy today and its cost.</summary>
public sealed class PowerModule : Module
{
    readonly TextBlock _total, _source, _today, _session;
    readonly StackBar _bar = new();
    readonly RowList _parts = new();
    readonly Sparkline _history = new("power.total", fixedMax: null);

    public PowerModule(ModuleConfig config) : base(config, "Power")
    {
        var stat = W.Stat("watts", out _total, out _source);
        var energy = W.SpacedColumns(W.Row("Today", out _today), W.Row("Since start", out _session));
        energy.Margin = new Thickness(0, 4, 0, 0);
        _bar.Margin = new Thickness(0, 6, 0, 4);
        View = MakeTile(Body(_history.ShowFrom(Density.Normal), stat, _bar, _parts.ShowFrom(Density.Roomy), energy.ShowFrom(Density.Normal)));
    }

    public override void Update(Snapshot s)
    {
        var p = s.Power;
        if (p == null)
            return;
        _total.Text = $"{p.TotalW:0} W";
        _source.Text = L.T(p.Source);
        _source.Foreground = p.Measured ? UI.Theme.Good : UI.Theme.Dim;
        _bar.Set(p.Parts.Select(x => (x.Watts, PowerText.BrushFor(x))).ToList());
        for (int i = 0; i < p.Parts.Count; i++)
        {
            var part = p.Parts[i];
            _parts.Set(i, L.T(part.Label), (part.Estimated ? "≈ " : "") + $"{part.Watts:0} W", valueColor: PowerText.BrushFor(part));
        }
        _parts.Trim(p.Parts.Count);
        _today.Text = string.Join(" · ", new[] { PowerText.Energy(p.TodayWh), PowerText.Cost(Config, p.TodayWh) }.Where(x => x != null));
        _session.Text = PowerText.Energy(p.SessionWh);
        _history.Refresh();
    }
}

/// <summary>Power as an arc gauge, speedometer or LED digits.</summary>
public sealed class PowerGaugeModule : Module
{
    readonly Gauge _gauge;
    readonly TextBlock _info = InfoLine();

    public PowerGaugeModule(ModuleConfig config, string style) : base(config, "Power")
    {
        double max = Math.Clamp(config.OptInt("Max", 600), 100, 3000);
        double major = max <= 400 ? 50 : max <= 1000 ? 100 : 250;
        _gauge = style switch
        {
            "speedometer" => new Speedometer { Label = L.T("WATTS"), Max = max, Major = major, Minor = major / 4, Redline = max * 0.85 },
            "segments" => new SevenSegment { Label = "SYSTEM W" },
            _ => new ArcGauge { Label = "SYSTEM", Unit = "W", Max = max, WarnAt = max * 0.7, CritAt = max * 0.9 },
        };
        View = GaugeTile(_gauge, _info);
    }

    public override void Update(Snapshot s)
    {
        var p = s.Power;
        if (p == null)
            return;
        if (_gauge is SevenSegment seg)
            seg.Value = Math.Min(9999, p.TotalW).ToString("0").PadLeft(3);
        else
            _gauge.Set("v", p.TotalW);
        string parts = string.Join(" ", p.Parts.Where(x => x.Label is "CPU" or "GPU" or "GPUs").Select(x => $"{x.Label} {(x.Estimated ? "≈" : "")}{x.Watts:0}"));
        _info.Text = Gauges.Join(L.T(p.Measured ? "measured" : "estimated"), parts, L.F("{0} today", PowerText.Energy(p.TodayWh)), PowerText.Cost(Config, p.TodayWh));
    }
}

