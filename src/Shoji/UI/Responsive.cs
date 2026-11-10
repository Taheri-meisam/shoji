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
