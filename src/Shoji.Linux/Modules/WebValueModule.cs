using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Shoji.Core;
using Shoji.Sensors;
using Shoji.UI;

namespace Shoji.Modules;

/// <summary>
/// One value from any web address or JSON API: big number with unit, label and a sparkline (classic), an arc gauge
/// between Min and Max, or LED digits. Text answers ("OK", "Off") are shown big, without a graph.
/// </summary>
public sealed class WebValueModule : OnlineModule
{
    readonly WebValueService? _service;
    readonly string _field = "";
    readonly int? _decimals;
    readonly double _min;
    readonly Gauge? _gauge;
    readonly Control? _numberView;
    readonly TextBlock _number = W.Number("", 52), _unit = W.Text("", UI.Theme.Medium, UI.Theme.Dim), _text;
    readonly Spark? _spark;
    readonly string? _headerProblem;
    int _shownVersion = -1;
    string? _shownError;
    WebReading? _reading;

    public WebValueModule(ModuleConfig config, string style) : base(config, "Web value")
    {
        string? url = config.Opt("Url");
        _text = W.Text("", 30, UI.Theme.Text, FontWeight.SemiBold);
        _text.TextWrapping = TextWrapping.Wrap;
        _text.TextAlignment = TextAlignment.Center;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.VerticalAlignment = VerticalAlignment.Center;
        _text.IsVisible = false;
        if (url == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            View = MakeTile(SetupHint(url == null ? "the web address (and the field to show)" : "a web address starting with https:// or http://"));
            return;
        }

        _field = config.Opt("Field") ?? "";
        _decimals = int.TryParse(config.Opt("Decimals"), out int d) ? Math.Clamp(d, 0, 6) : null;
        int seconds = Math.Clamp(config.OptInt("Every", 60), 10, 86400);
        string? header = config.Opt("Header");
        var parsed = WebValueService.ParseHeader(header);
        if (header != null && parsed == null)
            _headerProblem = L.T("Header must look like Name: value (not sent)");
        _service = ServiceStore.Get(WebValueService.Key(url, parsed == null ? null : header, seconds),
            () => new WebValueService(url, parsed, TimeSpan.FromSeconds(seconds)));

        string label = config.Opt("Label") ?? "", unit = config.Opt("Unit") ?? "";
        _min = Num(config.Opt("Min")) ?? 0;
        double max = Num(config.Opt("Max")) ?? 100;
        if (max <= _min)
            max = _min + 100;

        if (style is "arc" or "segments")
        {
            _gauge = style == "arc"
                ? new ArcGauge { Max = max - _min, WarnAt = double.MaxValue, CritAt = double.MaxValue, Unit = unit, Label = label, Format = v => WebValueText.Format(v + _min, _decimals) }
                : new SevenSegment { Label = string.Join(" ", new[] { label, unit }.Where(x => x.Length > 0)).ToUpperInvariant() };
            var face = new Grid();
            face.Children.Add(_gauge);
            face.Children.Add(_text);
            Status.TextAlignment = TextAlignment.Center;
            Status.HorizontalAlignment = HorizontalAlignment.Center;
            View = GaugeTile(face, Status, infoAlways: true);
            return;
        }

        // Classic: the number fills the width it can (long numbers shrink), unit beside it, label and graph below.
        _unit.Text = unit;
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(6, 0, 0, 10);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(_number);
        row.Children.Add(_unit);
        var labelText = W.Text(label, UI.Theme.Small + 1, UI.Theme.Dim);
        labelText.IsVisible = label.Length > 0;
        _numberView = W.Stack(new Viewbox { Child = row, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left }, labelText);
        _spark = new Spark { MinHeight = 24, Margin = new Thickness(0, 8, 0, 0) };
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Status, Dock.Bottom);
        body.Children.Add(Status);
        var fill = new Grid();
        fill.Children.Add(Body(_spark.ShowFrom(Density.Normal), _numberView));
        fill.Children.Add(_text);
        body.Children.Add(fill);
        View = MakeTile(body);
    }

    static double? Num(string? s) =>
        s != null && (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) ? v : null;

    public override void Update(Snapshot s)
    {
        if (_service == null)
            return;
        // Re-read only when a fetch finished (every 10 s at the most); the status line ages every refresh.
        if (_service.Version != _shownVersion || _service.Error != _shownError || _reading == null)
        {
            _shownVersion = _service.Version;
            _shownError = _service.Error;
            _reading = _service.Read(_field);
            Show(_reading);
        }
        var status = WebValueText.Status(_reading, _service.Interval, s.Time);
        Status.Text = status?.Text ?? _headerProblem ?? "";
        Status.Foreground = status?.Warn == true || status == null && _headerProblem != null ? UI.Theme.Warn : UI.Theme.Dim;
        Status.IsVisible = Status.Text.Length > 0;
    }

    void Show(WebReading r)
    {
        bool isText = r.Number == null && r.Text != null;
        _text.Text = r.Text ?? "";
        _text.FontSize = (r.Text?.Length ?? 0) <= 8 ? 34 : r.Text!.Length <= 20 ? 24 : UI.Theme.Medium;
        _text.IsVisible = isText;
        if (_gauge != null)
            _gauge.IsVisible = !isText;
        if (_numberView != null)
            _numberView.IsVisible = !isText;
        if (_spark != null)
        {
            _spark.Data = isText ? Array.Empty<double>() : r.History;
            _spark.InvalidateVisual(); // only when a new value came in, every 10 s at the most
        }
        if (r.Number is not double v)
        {
            _number.Text = "–";
            if (_gauge is SevenSegment none)
                none.Value = "--";
            return;
        }
        _number.Text = WebValueText.Format(v, _decimals);
        switch (_gauge)
        {
            case ArcGauge arc:
                arc.Set("v", v - _min);
                break;
            case SevenSegment seg:
                // Nine digits at most: drop the decimals first, and show dashes if it still doesn't fit.
                string digits = WebValueText.Format(v, _decimals, invariant: true);
                if (digits.Length > 9)
                    digits = WebValueText.Format(v, 0, invariant: true);
                seg.Value = digits.Length > 9 ? "---------" : digits;
                break;
        }
    }

    /// <summary>The recent values as a small line graph, scaled to their own range.</summary>
    sealed class Spark : DrawnControl
    {
        public double[] Data = Array.Empty<double>();
        readonly IBrush _stroke = UI.Theme.Solid(UI.Theme.AccentColor), _fill = Graph.Fade(UI.Theme.AccentColor, UI.Theme.Current.GraphFill);

        protected override void Draw(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0 || Data.Length < 2)
                return;
            double lo = Data.Min(), hi = Data.Max(), pad = Math.Max((hi - lo) * 0.15, Math.Abs(hi) * 0.001 + 1e-9);
            // The first values spread over the width, then it scrolls (the last 120 values).
            Graph.Draw(dc, Data, Data.Length, _stroke, _fill, hi + pad, w, h, lo - pad);
        }
    }
}
