using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>Your websites: up / down, speed, SSL and domain expiry, content check.</summary>
public sealed class WebsitesModule : Module
{
    sealed class Row
    {
        public required Ellipse Light { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Detail { get; init; }
        public required TextBlock Ms { get; init; }
        public required MiniGraph Graph { get; init; }
        public required Grid View { get; init; }
    }

    readonly StackPanel _rows = new();
    readonly TextBlock _summary;
    readonly List<Row> _views = new();
    readonly HashSet<string> _mine;

    public WebsitesModule(ModuleConfig config) : base(config, "Websites")
    {
        // This tile shows its own sites (the monitor checks the sites of every Websites tile together).
        _mine = (config.Sites ?? new List<SiteRef>()).Select(s => WebsiteMonitor.Normalize(s.Url)?.ToString()).Where(u => u != null).Select(u => u!).ToHashSet();
        _summary = W.Text("", Theme.Small, Theme.Dim);
        if (_mine.Count == 0)
        {
            var hint = W.Text(L.T("Add your sites in Settings → Pages & tiles → this Websites tile."), Theme.Small, Theme.Dim);
            hint.TextWrapping = TextWrapping.Wrap;
            View = MakeTile(hint);
            return;
        }
        View = MakeTile(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, _summary);
    }

    Row MakeRow()
    {
        var light = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = W.Text("", Theme.Small + 1.5, weight: FontWeights.SemiBold);
        var detail = W.Text("", Theme.Small - 1, Theme.Dim);
        var ms = W.Text("", Theme.Small + 1);
        ms.HorizontalAlignment = HorizontalAlignment.Right;
        ms.FontFamily = Theme.Mono;
        var graph = new MiniGraph { Width = 64, Height = 18, Margin = new Thickness(8, 0, 8, 0) };

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = W.Stack(name, detail.ShowFrom(Density.Normal));
        Grid.SetColumn(text, 1);
        Grid.SetColumn(graph, 2);
        Grid.SetColumn(ms, 3);
        grid.Children.Add(light);
        grid.Children.Add(text);
        grid.Children.Add(graph.ShowFrom(Density.Normal));
        grid.Children.Add(ms);
        _rows.Children.Add(grid);
        if (View is Tile t)
            Responsive.Apply(grid, t.Density);
        return new Row { Light = light, Name = name, Detail = detail, Ms = ms, Graph = graph, View = grid };
    }

    public override void Update(Snapshot s)
    {
        if (_mine.Count == 0)
            return;
        var sites = s.Sites.Where(x => _mine.Contains(x.Url)).ToList();
        while (_views.Count < sites.Count)
            _views.Add(MakeRow());
        // Sites removed (or not checked any more): drop their rows.
        while (_views.Count > sites.Count)
        {
            _rows.Children.Remove(_views[^1].View);
            _views.RemoveAt(_views.Count - 1);
        }

        int up = 0;
        for (int i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var v = _views[i];
            var color = site.State switch
            {
                SiteState.Up => Theme.Good,
                SiteState.Warning => Theme.Warn,
                SiteState.Down => Theme.Crit,
                _ => Theme.Dim,
            };
            if (site.State is SiteState.Up or SiteState.Warning) up++;
            v.Light.Fill = color;
            v.Name.Text = site.Name;
            v.Ms.Text = site.State == SiteState.Down ? L.T("DOWN") : site.Ms is double ms ? (ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms") : "…";
            v.Ms.Foreground = site.State == SiteState.Down ? Theme.Crit : site.Ms > 2000 ? Theme.Warn : Theme.Text;

            // Second line: the problem if there is one, otherwise the vital signs.
            if (site.Problem != null)
            {
                string since = site.DownSince is DateTime d ? L.F(" · down {0}", W.Duration(DateTime.UtcNow - d)) : "";
                v.Detail.Text = site.Problem + since;
                v.Detail.Foreground = color;
            }
            else
            {
                var parts = new List<string>();
                if (site.StatusCode is int code) parts.Add(code.ToString());
                if (site.UptimePercent is double u) parts.Add(L.F("{0:0.##}% up", u));
                if (site.CertDaysLeft is int cert) parts.Add($"SSL {cert} d");
                if (site.DomainDaysLeft is int domain) parts.Add(L.F("domain {0} d", domain));
                v.Detail.Text = parts.Count == 0 ? L.T("checking…") : string.Join(" · ", parts);
                v.Detail.Foreground = Theme.Dim;
            }
            v.Graph.Set(site.RecentMs, site.State == SiteState.Down ? Theme.Crit : Theme.Accent);
        }

        _summary.Text = sites.Count == 0 ? "" : up == sites.Count ? L.T("all up") : L.F("{0}/{1} up", up, sites.Count);
        _summary.Foreground = up == sites.Count ? Theme.Dim : Theme.Crit;
    }
}

/// <summary>Tiny response-time graph (no axes).</summary>
public sealed class MiniGraph : FrameworkElement
{
    IReadOnlyList<double> _data = Array.Empty<double>();
    Brush _brush = Theme.Accent;

    public void Set(IReadOnlyList<double> data, Brush brush)
    {
        _data = data;
        _brush = brush;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_data.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        double max = Math.Max(100, _data.Max() * 1.15);
        Graph.Draw(dc, _data, _data.Count, _brush, null, max, ActualWidth, ActualHeight);
    }
}
