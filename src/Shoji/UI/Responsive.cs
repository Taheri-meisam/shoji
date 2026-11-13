using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shoji.UI;

/// <summary>How much room a tile has, decided from its own size (like a CSS container query).</summary>
public enum Density { Compact, Normal, Roomy }

/// <summary>
/// Tiles show less when they're small instead of shrinking everything: an element marked
/// <c>.ShowFrom(Density.Normal)</c> (detail rows, graphs, legends…) is hidden while its tile is compact.
/// The tile works out its density whenever its size changes and applies it to everything inside.
/// </summary>
public static class Responsive
{
    public static readonly DependencyProperty ShowFromProperty = DependencyProperty.RegisterAttached(
        "ShowFrom", typeof(Density), typeof(Responsive), new PropertyMetadata(Density.Compact));

    /// <summary>Only show this element when its tile has at least this much room.</summary>
    public static T ShowFrom<T>(this T element, Density minimum) where T : UIElement
    {
        element.SetValue(ShowFromProperty, minimum);
        return element;
    }

    /// <summary>Density for a tile of this size (tile units: before the page's scale is applied).</summary>
    public static Density For(double width, double height) =>
        width < 240 || height < 165 ? Density.Compact
        : width >= 460 && height >= 300 ? Density.Roomy
        : Density.Normal;

    /// <summary>Shows / hides the marked elements under <paramref name="root"/> for the given density.</summary>
    public static void Apply(DependencyObject root, Density density)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d)
                continue;
            if (d is UIElement e && e.ReadLocalValue(ShowFromProperty) != DependencyProperty.UnsetValue)
                e.Visibility = density >= (Density)e.GetValue(ShowFromProperty) ? Visibility.Visible : Visibility.Collapsed;
            Apply(d, density);
        }
    }
}

/// <summary>
/// Shows its content whole or not at all: when the room left for it (say the bottom of a short tile) is less than
/// it needs, it is hidden instead of being cut through the middle.
/// </summary>
public sealed class WholeOrNothing : Decorator
{
    public WholeOrNothing(UIElement child)
    {
        Child = child;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;
        Child.Measure(new Size(available.Width, double.PositiveInfinity));
        var d = Child.DesiredSize;
        return new Size(d.Width, d.Height <= available.Height + 0.5 ? d.Height : 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child == null)
            return final;
        bool fits = Child.DesiredSize.Height <= final.Height + 0.5;
        Child.Opacity = fits ? 1 : 0;
        Child.Arrange(fits ? new Rect(final) : new Rect(0, 0, 0, 0));
        return final;
    }
}

/// <summary>Lists in tiles scroll by touch with no scroll bar; this fades the bottom edge while more is below.</summary>
public static class OverflowFade
{
    public static ScrollViewer FadeOverflow(this ScrollViewer sv)
    {
        bool faded = false;
        sv.ScrollChanged += (_, e) =>
        {
            double vh = e.ViewportHeight, more = e.ExtentHeight - e.VerticalOffset - vh;
            bool want = vh > 0 && more > 1;
            if (want == faded)
                return;
            faded = want;
            if (!want)
            {
                sv.OpacityMask = null;
                return;
            }
            double fade = System.Math.Min(0.4, 28 / vh);
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            mask.Freeze();
            sv.OpacityMask = mask;
        };
        return sv;
    }
}

/// <summary>
/// A gauge with its summary line ("65° · 4.91 GHz · 93 W"). Normally the line sits under the gauge; when the tile is
/// much wider than tall (a portrait screen's stacked rows, a bar display) the gauge moves left and the summary
/// becomes a column of readable numbers beside it, with the units dimmed, instead of a small line under an island.
/// </summary>
public sealed class GaugeWithInfo : Panel
{
    readonly UIElement _gauge;
    readonly TextBlock _info;
    readonly TextBlock _side = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    // Follows the summary's text (WPF's TextBlock has no change event; a binding notifies without holding the tile).
    readonly TextBlock _watch = new();
    string[] _parts = System.Array.Empty<string>();
    double _size;

    public GaugeWithInfo(UIElement gauge, TextBlock info)
    {
        _gauge = gauge;
        _info = info;
        Children.Add(gauge);
        Children.Add(info);
        Children.Add(_side);
        _watch.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = info, NotifyOnTargetUpdated = true });
        _watch.TargetUpdated += (_, _) => Sync();
        Sync();
    }

    void Sync()
    {
        var parts = (_info.Text ?? "").Split(" · ", System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (System.Linq.Enumerable.SequenceEqual(parts, _parts))
            return;
        _parts = parts;
        _size = 0; // rebuild at the next measure
        InvalidateMeasure();
    }

    bool Wide(Size s) => _parts.Length > 0 && s.Width >= s.Height * 2.1 && s.Height >= 64
                         && !double.IsInfinity(s.Width) && !double.IsInfinity(s.Height);

    /// <summary>Splits "4.91 GHz" into the number and the rest, so the unit can be dimmed.</summary>
    static (string Number, string Unit) Split(string part)
    {
        int i = 0;
        while (i < part.Length && (char.IsDigit(part[i]) || part[i] is '.' or ',' or '-' or '+' or '°' or '%' || i > 0 && part[i] == ':'))
            i++;
        return i == 0 ? ("", part) : (part[..i], part[i..]);
    }

    void Build(double size)
    {
        if (System.Math.Abs(size - _size) < 0.25)
            return;
        _size = size;
        _side.Inlines.Clear();
        for (int i = 0; i < _parts.Length; i++)
        {
            if (i > 0)
                _side.Inlines.Add(new System.Windows.Documents.LineBreak());
            var (num, rest) = Split(_parts[i]);
            if (num.Length > 0)
                _side.Inlines.Add(new System.Windows.Documents.Run(num)
                    { FontSize = size, FontFamily = Theme.Display, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
            if (rest.Length > 0)
                _side.Inlines.Add(new System.Windows.Documents.Run(rest)
                    { FontSize = num.Length > 0 ? size * 0.62 : size * 0.72, FontFamily = Theme.Font, Foreground = Theme.Dim });
        }
        _side.LineHeight = size * 1.28;
        _side.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    double SideSize(Size s) => System.Math.Clamp(System.Math.Min(s.Height / (_parts.Length * 1.45 + 0.4),
        s.Width * 0.5 / (System.Linq.Enumerable.Max(_parts, p => p.Length) * 0.5 + 1)), 12, 26);

    protected override Size MeasureOverride(Size available)
    {
        if (Wide(available))
        {
            Build(SideSize(available));
            _info.Measure(default);
            _side.Measure(new Size(available.Width * 0.55, available.Height));
            _gauge.Measure(new Size(System.Math.Min(available.Height * 1.25, available.Width * 0.5), available.Height));
            return available;
        }
        _side.Measure(default);
        _info.Measure(available);
        double ih = _info.Visibility == Visibility.Visible ? _info.DesiredSize.Height : 0;
        _gauge.Measure(new Size(available.Width, System.Math.Max(0, available.Height - ih)));
        return new Size(System.Math.Max(_gauge.DesiredSize.Width, _info.DesiredSize.Width), _gauge.DesiredSize.Height + ih);
    }

    protected override Size ArrangeOverride(Size final)
    {
        bool wide = Wide(final);
        _side.Opacity = wide ? 1 : 0; // opacity, not Visibility: no layout pass from inside the layout pass
        _info.Opacity = wide ? 0 : 1;
        if (wide)
        {
            // Gauge and numbers as one group, centred: the gauge square-ish, the column beside it.
            double gw = System.Math.Min(final.Height * 1.25, final.Width * 0.5);
            double tw = System.Math.Min(_side.DesiredSize.Width, final.Width - gw - 14);
            double x = System.Math.Max(0, (final.Width - gw - 14 - tw) / 2);
            _gauge.Arrange(new Rect(x, 0, gw, final.Height));
            double th = _side.DesiredSize.Height;
            _side.Arrange(new Rect(x + gw + 14, System.Math.Max(0, (final.Height - th) / 2), tw, System.Math.Min(th, final.Height)));
            _info.Arrange(new Rect(0, 0, 0, 0));
            return final;
        }
        double ih = _info.Visibility == Visibility.Visible ? _info.DesiredSize.Height : 0;
        _gauge.Arrange(new Rect(0, 0, final.Width, System.Math.Max(0, final.Height - ih)));
        _info.Arrange(new Rect(0, final.Height - ih, final.Width, ih));
        _side.Arrange(new Rect(0, 0, 0, 0));
        return final;
    }
}

/// <summary>
/// The filler at the bottom of a tile (a history graph) shown only when the room left reaches its minimum height:
/// below that the layout would push it over the rows above it instead.
/// </summary>
public sealed class FillOrNothing : System.Windows.Controls.Decorator
{
    public FillOrNothing(UIElement child) => Child = child;

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;
        Child.Measure(available);
        return new Size(Child.DesiredSize.Width, System.Math.Min(Child.DesiredSize.Height, available.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child == null)
            return final;
        double min = (Child as FrameworkElement)?.MinHeight ?? 0;
        bool fits = final.Height + 0.5 >= min;
        Child.Opacity = fits ? 1 : 0; // not Visibility: no layout pass from inside the layout pass
        Child.Arrange(fits ? new Rect(final) : new Rect(0, 0, 0, 0));
        return final;
    }
}

/// <summary>
/// Content that scales down as a whole when the room is shorter than it needs (the clock's big time and date on a
/// very small screen), instead of having its last line cut off. With enough room it changes nothing.
/// </summary>
public sealed class FitHeight : System.Windows.Controls.Decorator
{
    const double Least = 0.45;
    double _scale = 1;

    public FitHeight(UIElement child) => Child = child;

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;
        Child.Measure(new Size(available.Width, double.PositiveInfinity));
        _scale = 1;
        double need = Child.DesiredSize.Height;
        if (!double.IsInfinity(available.Height) && need > available.Height + 0.5 && need > 0)
        {
            _scale = System.Math.Max(Least, available.Height / need);
            // Scaled down it is also narrower, so it may use more width (text wraps less).
            Child.Measure(new Size(available.Width / _scale, double.PositiveInfinity));
        }
        return new Size(System.Math.Min(available.Width, Child.DesiredSize.Width * _scale), Child.DesiredSize.Height * _scale);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child == null)
            return final;
        Child.Arrange(new Rect(0, 0, final.Width / _scale, Child.DesiredSize.Height));
        Child.RenderTransform = _scale < 0.999 ? new System.Windows.Media.ScaleTransform(_scale, _scale) : null;
        return final;
    }
}
