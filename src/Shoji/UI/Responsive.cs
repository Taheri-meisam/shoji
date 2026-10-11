using System.Windows;

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
