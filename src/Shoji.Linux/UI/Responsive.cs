using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Shoji.UI;

/// <summary>How much room a tile has, decided from its own size (like a CSS container query).</summary>
public enum Density { Compact, Normal, Roomy }

/// <summary>
/// Tiles show less when they're small instead of shrinking everything: an element marked
/// <c>.ShowFrom(Density.Normal)</c> is hidden while its tile is compact. Same rules as on Windows.
/// </summary>
public static class Responsive
{
    public static readonly AttachedProperty<Density?> ShowFromProperty =
        AvaloniaProperty.RegisterAttached<Control, Density?>("ShowFrom", typeof(Responsive));

    public static T ShowFrom<T>(this T element, Density minimum) where T : Control
    {
        element.SetValue(ShowFromProperty, minimum);
        return element;
    }

    public static Density For(double width, double height) =>
        width < 240 || height < 165 ? Density.Compact
        : width >= 460 && height >= 300 ? Density.Roomy
        : Density.Normal;

    public static void Apply(ILogical root, Density density)
    {
        foreach (var child in root.GetLogicalChildren())
        {
            if (child is Control c && c.GetValue(ShowFromProperty) is Density min)
                c.IsVisible = density >= min;
            Apply(child, density);
        }
    }
}

/// <summary>
/// Shows its content whole or not at all: when the room left for it (say the bottom of a short tile) is less than
/// it needs, it is hidden instead of being cut through the middle.
/// </summary>
public sealed class WholeOrNothing : Decorator
{
    public WholeOrNothing(Control child)
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
        Child.Opacity = fits ? 1 : 0; // not IsVisible: that would re-run layout, which would show it again
        Child.Arrange(fits ? new Rect(final) : default);
        return final;
    }
}

/// <summary>Lists in tiles scroll by touch with no scroll bar; this fades the bottom edge while more is below.</summary>
public static class OverflowFade
{
    public static ScrollViewer FadeOverflow(this ScrollViewer sv)
    {
        bool faded = false;
        void Update()
        {
            double vh = sv.Viewport.Height, more = sv.Extent.Height - sv.Offset.Y - vh;
            bool want = vh > 0 && more > 1;
            if (want == faded)
                return;
            faded = want;
            double fade = vh > 0 ? Math.Min(0.4, 28 / vh) : 0.2;
            sv.OpacityMask = !want ? null : new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 1 - fade), new GradientStop(Colors.Transparent, 1) },
            };
        }
        sv.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty || e.Property == ScrollViewer.OffsetProperty)
                Update();
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
    readonly Control _gauge;
    readonly TextBlock _info;
    readonly TextBlock _side = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    string[] _parts = Array.Empty<string>();
    double _size;

    public GaugeWithInfo(Control gauge, TextBlock info)
    {
        _gauge = gauge;
        _info = info;
        Children.Add(gauge);
        Children.Add(info);
        Children.Add(_side);
        info.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
                Sync();
        };
        Sync();
    }

    void Sync()
    {
        var parts = (_info.Text ?? "").Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.AsSpan().SequenceEqual(_parts))
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
        while (i < part.Length && (char.IsDigit(part[i]) || part[i] is '.' or ',' or '-' or '+' or '°' or '%' || i > 0 && part[i] == ':' ))
            i++;
        return i == 0 ? ("", part) : (part[..i], part[i..]);
    }

    void Build(double size)
    {
        if (Math.Abs(size - _size) < 0.25)
            return;
        _size = size;
        var inlines = _side.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        inlines.Clear();
        for (int i = 0; i < _parts.Length; i++)
        {
            if (i > 0)
                inlines.Add(new Avalonia.Controls.Documents.LineBreak());
            var (num, rest) = Split(_parts[i]);
            if (num.Length > 0)
                inlines.Add(new Avalonia.Controls.Documents.Run(num)
                    { FontSize = size, FontFamily = UI.Theme.Display, FontWeight = FontWeight.SemiBold, Foreground = UI.Theme.Text });
            if (rest.Length > 0)
                inlines.Add(new Avalonia.Controls.Documents.Run(rest)
                    { FontSize = num.Length > 0 ? size * 0.62 : size * 0.72, FontFamily = UI.Theme.Font, Foreground = UI.Theme.Dim });
        }
        _side.LineHeight = size * 1.28;
    }

    double SideSize(Size s) => Math.Clamp(Math.Min(s.Height / (_parts.Length * 1.45 + 0.4), s.Width * 0.5 / (System.Linq.Enumerable.Max(_parts, p => p.Length) * 0.5 + 1)), 12, 26);

    protected override Size MeasureOverride(Size available)
    {
        if (Wide(available))
        {
            Build(SideSize(available));
            _info.Measure(default);
            _side.Measure(new Size(available.Width * 0.55, available.Height));
            _gauge.Measure(new Size(Math.Min(available.Height * 1.25, available.Width * 0.5), available.Height));
            return available;
        }
        _side.Measure(default);
        _info.Measure(available);
        double ih = _info.IsVisible ? _info.DesiredSize.Height : 0;
        _gauge.Measure(new Size(available.Width, Math.Max(0, available.Height - ih)));
        return new Size(Math.Max(_gauge.DesiredSize.Width, _info.DesiredSize.Width), _gauge.DesiredSize.Height + ih);
    }

    protected override Size ArrangeOverride(Size final)
    {
        bool wide = Wide(final);
        _side.Opacity = wide ? 1 : 0; // opacity, not IsVisible: no layout pass from inside the layout pass
        _info.Opacity = wide ? 0 : 1;
        if (wide)
        {
            // Gauge and numbers as one group, centred: the gauge square-ish, the column beside it.
            double gw = Math.Min(final.Height * 1.25, final.Width * 0.5);
            double tw = Math.Min(_side.DesiredSize.Width, final.Width - gw - 14);
            double x = Math.Max(0, (final.Width - gw - 14 - tw) / 2);
            _gauge.Arrange(new Rect(x, 0, gw, final.Height));
            double th = _side.DesiredSize.Height;
            _side.Arrange(new Rect(x + gw + 14, Math.Max(0, (final.Height - th) / 2), tw, Math.Min(th, final.Height)));
            _info.Arrange(default);
            return final;
        }
        double ih = _info.IsVisible ? _info.DesiredSize.Height : 0;
        _gauge.Arrange(new Rect(0, 0, final.Width, Math.Max(0, final.Height - ih)));
        _info.Arrange(new Rect(0, final.Height - ih, final.Width, ih));
        _side.Arrange(default);
        return final;
    }
}

/// <summary>
/// The filler at the bottom of a tile (a history graph) shown only when the room left reaches its minimum height:
/// below that the layout would centre it over the rows above it instead.
/// </summary>
public sealed class FillOrNothing : Decorator
{
    public FillOrNothing(Control child) => Child = child;

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;
        Child.Measure(available);
        return new Size(Child.DesiredSize.Width, Math.Min(Child.DesiredSize.Height, available.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child == null)
            return final;
        bool fits = final.Height + 0.5 >= Child.MinHeight;
        Child.Opacity = fits ? 1 : 0; // not IsVisible: no layout pass from inside the layout pass
        Child.Arrange(fits ? new Rect(final) : default);
        return final;
    }
}

/// <summary>
/// Content that scales down as a whole when the room is shorter than it needs (the clock's big time and date on a
/// very small screen), instead of having its last line cut off. With enough room it changes nothing.
/// </summary>
public sealed class FitHeight : Decorator
{
    const double Least = 0.45;
    double _scale = 1;

    public FitHeight(Control child) => Child = child;

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;
        Child.Measure(new Size(available.Width, double.PositiveInfinity));
        _scale = 1;
        double need = Child.DesiredSize.Height;
        if (!double.IsInfinity(available.Height) && need > available.Height + 0.5 && need > 0)
        {
            _scale = Math.Max(Least, available.Height / need);
            // Scaled down it is also narrower, so it may use more width (text wraps less).
            Child.Measure(new Size(available.Width / _scale, double.PositiveInfinity));
        }
        return new Size(Math.Min(available.Width, Child.DesiredSize.Width * _scale), Child.DesiredSize.Height * _scale);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child == null)
            return final;
        Child.Arrange(new Rect(0, 0, final.Width / _scale, Child.DesiredSize.Height));
        Child.RenderTransformOrigin = RelativePoint.TopLeft;
        Child.RenderTransform = _scale < 0.999 ? new ScaleTransform(_scale, _scale) : null;
        return final;
    }
}
