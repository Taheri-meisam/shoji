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
